package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"
)

type nativeHelper struct {
	cmd      *exec.Cmd
	stdin    io.WriteCloser
	stderr   bytes.Buffer
	token    string
	endpoint string
	done     chan error
}

func isolatedHelperEnv(token string) []string {
	env := []string{}
	for _, item := range os.Environ() {
		key, _, _ := strings.Cut(item, "=")
		switch strings.ToUpper(key) {
		case "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "DANMU_OUTBOUND_TOKEN":
			continue
		}
		env = append(env, item)
	}
	return append(env, "DANMU_OUTBOUND_TOKEN="+token)
}
func startNativeHelper(t *testing.T, mode, doh string) *nativeHelper {
	t.Helper()
	exe := os.Getenv("DANMU_OUTBOUND_HELPER_EXE")
	if exe == "" {
		t.Skip("set DANMU_OUTBOUND_HELPER_EXE for actual Windows process verification")
	}
	secret := make([]byte, 32)
	if _, err := rand.Read(secret); err != nil {
		t.Fatal(err)
	}
	h := &nativeHelper{token: hex.EncodeToString(secret), done: make(chan error, 1)}
	h.cmd = exec.Command(exe, "--http-version", mode, "--connect-timeout-ms", "5000", "--doh-url", doh)
	h.cmd.Env = isolatedHelperEnv(h.token)
	h.cmd.Stderr = &h.stderr
	var err error
	h.stdin, err = h.cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	stdout, err := h.cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err = h.cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if h.stdin == nil {
			return
		}
		if err := h.stdin.Close(); err != nil {
			t.Errorf("owned helper stdin close: %v", err)
		}
		h.stdin = nil
		select {
		case err := <-h.done:
			if err != nil {
				t.Errorf("helper exit: %v stderr=%s", err, safeMessage(fmt.Errorf("%s", h.stderr.String()), h.token))
			}
		case <-time.After(10 * time.Second):
			if err := h.cmd.Process.Kill(); err != nil {
				t.Errorf("owned helper kill: %v", err)
			}
			<-h.done
			t.Error("helper did not exit after stdin EOF")
		}
	})
	ready := make(chan string, 1)
	go func() {
		scanner := bufio.NewScanner(stdout)
		if scanner.Scan() {
			ready <- scanner.Text()
		} else {
			ready <- ""
		}
		for scanner.Scan() {
		}
	}()
	go func() { h.done <- h.cmd.Wait() }()
	select {
	case line := <-ready:
		var record struct {
			Ready           bool
			AppProtocol     int
			ProtocolVersion int
			PID             int
			Version         string
			URL             string
		}
		if err := json.Unmarshal([]byte(line), &record); err != nil {
			t.Fatalf("invalid startup record: %v", err)
		}
		parsed, err := url.Parse(record.URL)
		if err != nil || !record.Ready || record.AppProtocol != 1 || record.ProtocolVersion != 1 || record.PID != h.cmd.Process.Pid || record.Version != "1.0.1+6004732" || parsed.Hostname() != "127.0.0.1" {
			t.Fatalf("invalid startup identity: %+v", record)
		}
		h.endpoint = record.URL
	case <-time.After(10 * time.Second):
		t.Fatal("no helper startup record")
	}
	return h
}
func (h *nativeHelper) request(t *testing.T, target string) (helperResponse, int) {
	t.Helper()
	payload, err := json.Marshal(helperRequest{URL: target, Method: "GET", TimeoutMS: 20000, Headers: [][2]string{{"User-Agent", "danmu-api-windows/connectivity"}}})
	if err != nil {
		t.Fatal(err)
	}
	req, err := http.NewRequest("POST", h.endpoint+"/request", bytes.NewReader(payload))
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Authorization", "Bearer "+h.token)
	client := &http.Client{Transport: &http.Transport{Proxy: nil}, Timeout: 25 * time.Second}
	defer client.CloseIdleConnections()
	response, err := client.Do(req)
	if err != nil {
		t.Fatalf("local helper HTTP: %v", err)
	}
	defer response.Body.Close()
	var result helperResponse
	if err := json.NewDecoder(response.Body).Decode(&result); err != nil {
		t.Fatal(err)
	}
	return result, response.StatusCode
}
func TestNativeHelperLifecycleAndAuthentication(t *testing.T) {
	for cycle := 0; cycle < 20; cycle++ {
		t.Run(fmt.Sprintf("cycle-%02d", cycle+1), func(t *testing.T) {
			h := startNativeHelper(t, "auto", "")
			client := &http.Client{Transport: &http.Transport{Proxy: nil}, Timeout: 2 * time.Second}
			defer client.CloseIdleConnections()
			for _, auth := range []string{"Bearer wrong", "Bearer " + h.token} {
				req, _ := http.NewRequest("GET", h.endpoint+"/health", nil)
				req.Header.Set("Authorization", auth)
				response, err := client.Do(req)
				if err != nil {
					t.Fatal(err)
				}
				data, err := io.ReadAll(response.Body)
				response.Body.Close()
				if err != nil {
					t.Fatal(err)
				}
				if auth == "Bearer wrong" {
					if response.StatusCode != 401 {
						t.Fatal("wrong token accepted")
					}
				} else {
					var health struct {
						ProtocolVersion int
						PID             int
						Version         string
					}
					if err := json.Unmarshal(data, &health); err != nil {
						t.Fatal(err)
					}
					if response.StatusCode != 200 || health.PID != h.cmd.Process.Pid || health.ProtocolVersion != 1 || health.Version != "1.0.1+6004732" {
						t.Fatal("health process identity mismatch")
					}
				}
			}
			result, status := h.request(t, "https://unlisted.invalid/test?token=not-logged")
			if status != 403 || result.Success || result.Phase != "policy" || result.ErrorCode != "target_not_allowed" {
				t.Fatal("unknown host not rejected explicitly")
			}
			if err := h.stdin.Close(); err != nil {
				t.Fatal(err)
			}
			h.stdin = nil
			select {
			case err := <-h.done:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(5 * time.Second):
				h.cmd.Process.Kill()
				<-h.done
				t.Fatal("stdin EOF failed")
			}
			response, err := client.Get(h.endpoint + "/health")
			if response != nil {
				response.Body.Close()
			}
			if err == nil {
				t.Fatal("loopback listener remains after helper exit")
			}
			t.Logf("pid=%d stdinEOF exitCode=0 portReleased=true", h.cmd.Process.Pid)
		})
	}
}
func TestNativeHelperMissingTokenStartup(t *testing.T) {
	exe := os.Getenv("DANMU_OUTBOUND_HELPER_EXE")
	if exe == "" {
		t.Skip("actual helper binary opt-in")
	}
	cmd := exec.Command(exe, "--http-version", "h3", "--connect-timeout-ms", "5000", "--doh-url", "")
	cmd.Env = isolatedHelperEnv("")
	output, err := cmd.CombinedOutput()
	if err == nil || !strings.Contains(string(output), "missing helper authentication token") {
		t.Fatalf("missing token startup must fail: %v %s", err, string(output))
	}
	t.Logf("missing token exitCode=%d", cmd.ProcessState.ExitCode())
}
func TestNativeHelperExplicitDNSFailure(t *testing.T) {
	h := startNativeHelper(t, "h3", "https://127.0.0.1:1/dns-query?token=private-doh-query")
	result, status := h.request(t, "https://api.gamer.com.tw/test?token=private-business-query")
	if status != 502 || result.Success || result.Phase != "DNS" || result.Message == "" || strings.Contains(result.Message, "private-") || strings.Contains(result.Message, "dns-query?") {
		t.Fatalf("DNS failure lost or leaked: %+v", result)
	}
	t.Logf("host=%s helperStatus=%d protocol=%s ech=%t phase=%s code=%s message=%s", result.Host, status, result.Protocol, result.ECH, result.Phase, result.ErrorCode, result.Message)
}
func TestNativeHelperLiveRequests(t *testing.T) {
	if os.Getenv("DANMU_OUTBOUND_LIVE_REQUESTS") != "1" {
		t.Skip("full HTTP live requests are opt-in; handshake test is not a substitute")
	}
	targets := []string{
		"https://api.gamer.com.tw/mobile_app/anime/v1/search.php?kw=Frieren",
		"https://api.tmdb.org/3/configuration",
		"https://api.themoviedb.org/3/configuration",
		"https://api.danmaku.weeblify.app/ddp/v1?path=/v2/search/anime?keyword=Frieren",
		"https://nipaplay.aimes-soft.com/v2/search/anime?keyword=Frieren",
		"https://api.animeko.org/v2/subjects/400602",
		"https://danmaku-global.myani.org/v2/subjects/400602",
		"https://danmaku-cn.myani.org/v2/subjects/400602",
		"https://s1.animeko.openani.org/v2/subjects/400602",
		"https://api.bangumi.vip/v0/episodes?subject_id=400602&limit=1",
	}
	fullH3ECH := false
	for _, mode := range []string{"h3", "auto"} {
		t.Run(mode, func(t *testing.T) {
			h := startNativeHelper(t, mode, os.Getenv("DANMU_OUTBOUND_LIVE_DOH"))
			for _, target := range targets {
				result, helperStatus := h.request(t, target)
				if result.Success {
					body, err := base64.StdEncoding.DecodeString(result.Body)
					if err != nil {
						t.Fatal(err)
					}
					if result.Protocol != "h3" && result.Protocol != "h2" {
						t.Fatal("invalid actual protocol")
					}
					if mode == "h3" && result.Protocol != "h3" {
						t.Fatal("forced h3 downgraded")
					}
					if targetPolicies[result.Host].requireECH && !result.ECH {
						t.Fatal("required ECH downgraded")
					}
					if result.Protocol == "h3" && result.ECH && len(body) > 0 {
						fullH3ECH = true
					}
					t.Logf("ACTUAL mode=%s host=%s helperStatus=%d protocol=%s ech=%t status=%d durationMs=%d bodyBytes=%d", mode, result.Host, helperStatus, result.Protocol, result.ECH, result.Status, result.DurationMS, len(body))
				} else {
					if result.Phase == "" || result.ErrorCode == "" || result.Message == "" {
						t.Fatal("failure missing diagnostic")
					}
					t.Logf("ACTUAL mode=%s host=%s helperStatus=%d protocol=%s ech=%t status=%d durationMs=%d phase=%s code=%s message=%s", mode, result.Host, helperStatus, result.Protocol, result.ECH, result.Status, result.DurationMS, result.Phase, result.ErrorCode, result.Message)
				}
			}
		})
	}
	if !fullH3ECH {
		t.Error("live gate unmet: no complete response on one actual h3 + ECH-accepted connection; see ACTUAL per-host boundaries")
	}
}
