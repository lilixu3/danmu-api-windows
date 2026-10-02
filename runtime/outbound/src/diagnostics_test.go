package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

// Proxy upload deadlines require a real server connection, not a Recorder.
func recordHelperResponse(t *testing.T, handler http.Handler, request *http.Request) *httptest.ResponseRecorder {
	t.Helper()
	server := httptest.NewServer(handler)
	defer server.Close()
	origin, err := url.Parse(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	request.URL.Scheme, request.URL.Host, request.RequestURI = origin.Scheme, origin.Host, ""
	response, err := server.Client().Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	recorder := httptest.NewRecorder()
	for key, values := range response.Header {
		recorder.Header()[key] = values
	}
	recorder.WriteHeader(response.StatusCode)
	if _, err = io.Copy(recorder, response.Body); err != nil {
		t.Fatal(err)
	}
	return recorder
}
func diagnosticTransport(t *testing.T, protocol string, ech bool, failure error) *outboundTransport {
	t.Helper()
	tr := newOutboundTransport(newResolver("", time.Second), "h3", time.Second)
	tr.resolve = func(context.Context, string) (targetRecords, error) {
		if failure != nil {
			return targetRecords{}, failure
		}
		return targetRecords{ech: []byte{1}}, nil
	}
	tr.dial = func(context.Context, string, targetRecords, string) (*session, error) {
		s := stubSession(protocol)
		s.ech = ech
		s.rt = roundTripFunc(func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: 401, Header: http.Header{"X-Danmu-Outbound-Protocol": {"spoofed"}, "X-Danmu-Outbound-Ech": {"spoofed"}, "Set-Cookie": {"a=1", "b=2"}}, Body: io.NopCloser(strings.NewReader("actual body"))}, nil
		})
		return s, nil
	}
	t.Cleanup(tr.close)
	return tr
}
func TestActualRequestMetadataAndProxyReservedHeaders(t *testing.T) {
	for _, caseValue := range []struct {
		protocol string
		ech      bool
	}{{"h3", true}, {"h2", false}} {
		t.Run(caseValue.protocol, func(t *testing.T) {
			token := "test-session-token-01234567890123456789"
			handler := helperHandler(token, diagnosticTransport(t, caseValue.protocol, caseValue.ech, nil))
			r := httptest.NewRequest("POST", "http://localhost/request", strings.NewReader(`{"url":"https://api.gamer.com.tw/test","method":"GET","timeoutMs":1000,"headers":[],"body":""}`))
			r.Header.Set("Authorization", "Bearer "+token)
			w := recordHelperResponse(t, handler, r)
			var result helperResponse
			if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
				t.Fatal(err)
			}
			if w.Code != 200 || !result.Success || result.Status != 401 || result.Protocol != caseValue.protocol || result.ECH != caseValue.ech || result.Host != "api.gamer.com.tw" {
				t.Fatalf("incorrect connection metadata: %+v", result)
			}
			if !strings.Contains(w.Body.String(), `"ech":`) {
				t.Fatal("false ECH must remain explicit")
			}
			for _, h := range result.Headers {
				if strings.HasPrefix(strings.ToLower(h[0]), internalPrefix) {
					t.Fatal("spoofed internal metadata in /request headers")
				}
			}
			r = proxyRequest(t, "http://localhost", token, "https://api.gamer.com.tw/test")
			w = recordHelperResponse(t, handler, r)
			if w.Code != 401 || w.Header().Get("X-Danmu-Outbound-Protocol") != caseValue.protocol || w.Header().Get("X-Danmu-Outbound-Ech") != fmt.Sprint(caseValue.ech) || w.Body.String() != "actual body" || len(w.Header().Values("Set-Cookie")) != 2 {
				t.Fatal("proxy metadata or upstream body/headers changed")
			}
		})
	}
}
func TestFailurePreservesReasonAndRedactsSecretsOnBothEndpoints(t *testing.T) {
	token := "private-helper-token-01234567890123456789"
	reason := errors.New("DoH Post \"https://dns.example/dns-query?token=query-secret\": read udp 203.0.113.1:443: timeout; Cookie=cookie-secret; Bearer auth-secret; " + token)
	handler := helperHandler(token, diagnosticTransport(t, "h3", true, reason))
	for _, endpoint := range []string{"request", "proxy"} {
		var r *http.Request
		if endpoint == "request" {
			r = httptest.NewRequest("POST", "http://localhost/request", strings.NewReader(`{"url":"https://api.gamer.com.tw/test?key=query-secret","method":"GET","timeoutMs":1000,"headers":[["Cookie","cookie-secret"],["Authorization","Bearer auth-secret"]],"body":""}`))
			r.Header.Set("Authorization", "Bearer "+token)
		} else {
			r = proxyRequest(t, "http://localhost", token, "https://api.gamer.com.tw/test?key=query-secret")
			r.Header.Set("Cookie", "cookie-secret")
			r.Header.Set("Authorization", "Bearer auth-secret")
		}
		w := recordHelperResponse(t, handler, r)
		var result helperResponse
		if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
			t.Fatal(err)
		}
		if w.Code != 502 || result.Success || result.Phase != "DNS" || result.ErrorCode != "dns_failed" || !strings.Contains(result.Message, "read udp") || !strings.Contains(result.Message, "timeout") {
			t.Fatalf("original failure lost: %+v", result)
		}
		for _, secret := range []string{token, "query-secret", "cookie-secret", "auth-secret", "dns-query?"} {
			if strings.Contains(w.Body.String(), secret) {
				t.Fatalf("diagnostic leaked %s", secret)
			}
		}
	}
}
func TestHealthAuthenticationAndProcessIdentity(t *testing.T) {
	token := "health-session-token-01234567890123456789"
	handler := helperHandler(token, diagnosticTransport(t, "h3", true, nil))
	for _, authorization := range []string{"", "Bearer wrong", token, "Bearer " + token} {
		r := httptest.NewRequest("GET", "http://localhost/health", nil)
		r.Header.Set("Authorization", authorization)
		w := recordHelperResponse(t, handler, r)
		if authorization == "Bearer "+token {
			var result struct {
				Success         bool
				ProtocolVersion int
				PID             int
				Version         string
			}
			if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
				t.Fatal(err)
			}
			if w.Code != 200 || !result.Success || result.ProtocolVersion != 1 || result.PID <= 0 || result.Version == "" {
				t.Fatalf("health identity invalid: %s", w.Body.String())
			}
		} else if w.Code != 401 || strings.Contains(w.Body.String(), token) {
			t.Fatal("health requires strict bearer authentication")
		}
	}
}
func TestResponseBodyReadFailureIsExplicit(t *testing.T) {
	token := "body-session-token-01234567890123456789"
	tr := diagnosticTransport(t, "h2", false, nil)
	tr.dial = func(context.Context, string, targetRecords, string) (*session, error) {
		s := stubSession("h2")
		s.rt = roundTripFunc(func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: 200, Header: make(http.Header), Body: brokenBody{}}, nil
		})
		return s, nil
	}
	r := httptest.NewRequest("POST", "http://localhost/request", strings.NewReader(`{"url":"https://api.tmdb.org/test","method":"GET","timeoutMs":1000,"headers":[],"body":""}`))
	r.Header.Set("Authorization", "Bearer "+token)
	w := httptest.NewRecorder()
	helperHandler(token, tr).ServeHTTP(w, r)
	var result helperResponse
	if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if result.Success || result.ErrorCode != "body_read_failed" || result.Phase != "response body" || result.Protocol != "h2" || !strings.Contains(result.Message, "stream reset proof") {
		t.Fatalf("body failure lost: %+v", result)
	}
}

type brokenBody struct{}

func (brokenBody) Read([]byte) (int, error) { return 0, errors.New("stream reset proof") }
func (brokenBody) Close() error             { return nil }
