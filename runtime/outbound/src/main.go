package main

import (
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"runtime"
	"strings"
	"syscall"
	"time"
)

const maxBody = 32 << 20

type helperRequest struct {
	URL       string      `json:"url"`
	Method    string      `json:"method"`
	Headers   [][2]string `json:"headers"`
	Body      string      `json:"body"`
	TimeoutMS int64       `json:"timeoutMs"`
}
type helperResponse struct {
	Success    bool        `json:"success"`
	Status     int         `json:"status"`
	Headers    [][2]string `json:"headers"`
	Body       string      `json:"body"`
	Host       string      `json:"host,omitempty"`
	DurationMS int64       `json:"durationMs"`
	Protocol   string      `json:"protocol"`
	ECH        bool        `json:"ech"`
	Phase      string      `json:"phase,omitempty"`
	ErrorCode  string      `json:"errorCode,omitempty"`
	Code       string      `json:"code,omitempty"`
	Message    string      `json:"message,omitempty"`
	Error      string      `json:"error,omitempty"`
}

func allowedTarget(u *url.URL) bool {
	if u == nil || u.Scheme != "https" || u.User != nil || u.Port() != "" && u.Port() != "443" || u.Fragment != "" {
		return false
	}
	_, ok := targetPolicies[u.Hostname()]
	return ok
}
func writeJSON(w http.ResponseWriter, status int, result any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(result); err != nil {
		log.Printf("phase=local_response errorCode=write_failed message=%q", safeMessage(err))
	}
}
func respond(w http.ResponseWriter, status int, result helperResponse) { writeJSON(w, status, result) }
func authorized(r *http.Request, token string) bool {
	return subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+token)) == 1
}
func helperHandler(token string, transport *outboundTransport) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, local *http.Request) {
		if local.URL.Path == "/proxy" {
			proxyHandler(token, transport).ServeHTTP(w, local)
			return
		}
		if local.URL.Path != "/request" && local.URL.Path != "/health" {
			http.NotFound(w, local)
			return
		}
		if !authorized(local, token) {
			respond(w, 401, invalid("authentication", "unauthorized", "helper authentication failed"))
			return
		}
		if local.URL.Path == "/health" {
			if local.Method != "GET" {
				respond(w, 405, invalid("validation", "method_not_allowed", "GET required"))
				return
			}
			writeJSON(w, 200, map[string]any{"success": true, "protocolVersion": protocolVersion, "pid": os.Getpid(), "version": buildVersion})
			return
		}
		if local.Method != "POST" {
			respond(w, 405, invalid("validation", "method_not_allowed", "POST required"))
			return
		}
		input, err := decodeHelperRequest(http.MaxBytesReader(w, local.Body, (maxBody*4/3)+(1<<20)))
		if err != nil {
			respond(w, 400, failureResult("validation", "invalid_request", err, token))
			return
		}
		u, err := url.Parse(input.URL)
		if err != nil || !allowedTarget(u) {
			respond(w, 403, invalid("policy", "target_not_allowed", "target not allowed"))
			return
		}
		switch input.Method {
		case "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS":
		default:
			respond(w, 400, invalid("validation", "method_not_allowed", "method not allowed"))
			return
		}
		if input.TimeoutMS <= 0 || input.TimeoutMS > 3600000 {
			respond(w, 400, invalid("validation", "invalid_deadline", "invalid request deadline"))
			return
		}
		body, err := base64.StdEncoding.DecodeString(input.Body)
		if err != nil {
			respond(w, 400, failureResult("validation", "invalid_body", err, token))
			return
		}
		if len(body) > maxBody {
			respond(w, 400, invalid("validation", "body_too_large", "request body exceeds 32 MiB"))
			return
		}
		ctx, cancel := context.WithTimeout(local.Context(), time.Duration(input.TimeoutMS)*time.Millisecond)
		defer cancel()
		request, err := http.NewRequestWithContext(ctx, input.Method, u.String(), bytes.NewReader(body))
		if err != nil {
			respond(w, 400, invalid("validation", "invalid_request", "invalid upstream request"))
			return
		}
		for _, header := range input.Headers {
			lower := strings.ToLower(header[0])
			if strings.HasPrefix(lower, internalPrefix) {
				continue
			}
			switch lower {
			case "host", "connection", "proxy-connection", "proxy-authorization", "transfer-encoding", "content-length", "accept-encoding", "upgrade", "te", "trailer":
				continue
			}
			request.Header.Add(header[0], header[1])
		}
		secrets := append(requestSecrets(token, request), string(body), input.Body)
		start := time.Now()
		fail := func(status int, result helperResponse, connection *session) {
			addResultMetadata(&result, u.Hostname(), start, connection)
			logFailure(u.Hostname(), result)
			respond(w, status, result)
		}
		response, connection, err := transport.roundTrip(request)
		if err != nil {
			fail(502, transportFailure(ctx, connection, err, secrets...), connection)
			return
		}
		defer logClose(u.Hostname(), response.Body, secrets...)
		data, err := io.ReadAll(io.LimitReader(response.Body, maxBody+1))
		if err != nil {
			result := transportFailure(ctx, connection, err, secrets...)
			result.Phase = "response body"
			if result.ErrorCode == "request_failed" {
				result.ErrorCode, result.Code = "body_read_failed", "body_read_failed"
			}
			fail(502, result, connection)
			return
		}
		if len(data) > maxBody {
			fail(502, invalid("response body", "body_too_large", "upstream response exceeds 32 MiB"), connection)
			return
		}
		if ctx.Err() != nil {
			fail(504, transportFailure(ctx, connection, ctx.Err(), secrets...), connection)
			return
		}
		headers := make([][2]string, 0)
		skip := hopHeaders(response.Header)
		for name, values := range response.Header {
			if skip[strings.ToLower(name)] || strings.EqualFold(name, "Content-Length") || strings.HasPrefix(strings.ToLower(name), internalPrefix) {
				continue
			}
			for _, value := range values {
				headers = append(headers, [2]string{name, value})
			}
		}
		result := helperResponse{Success: true, Status: response.StatusCode, Headers: headers, Body: base64.StdEncoding.EncodeToString(data)}
		addResultMetadata(&result, u.Hostname(), start, connection)
		log.Printf("host=%s protocol=%s ech=%t status=%d duration=%s", u.Hostname(), connection.protocol, connection.ech, response.StatusCode, time.Since(start).Round(time.Millisecond))
		respond(w, 200, result)
	})
}
func main() {
	if err := run(); err != nil {
		log.Fatal(safeMessage(err, os.Getenv("DANMU_OUTBOUND_TOKEN")))
	}
}
func run() error {
	if runtime.GOOS == "android" && os.Getenv("SSL_CERT_DIR") == "" {
		if err := os.Setenv("SSL_CERT_DIR", "/apex/com.android.conscrypt/cacerts:/system/etc/security/cacerts"); err != nil {
			return fmt.Errorf("certificate directory: %w", err)
		}
	}
	version := flag.String("http-version", "auto", "auto, h2 or h3")
	timeoutMS := flag.Int("connect-timeout-ms", 3000, "connection budget in milliseconds")
	dohURL := flag.String("doh-url", "", "custom DNS-over-HTTPS endpoint")
	boundedLogPath := flag.String("bounded-log", "", "bounded local frpc log sink")
	githubProxy := flag.Bool("github-proxy", false, "App GitHub CONNECT proxy (independent of Node)")
	securePath := flag.String("secure-directory", "", "Windows private session directory (offline ACL mode)")
	secureMode := flag.String("secure-directory-mode", "", "prepare or verify (no server, no token)")
	flag.Parse()
	if *securePath != "" || *secureMode != "" {
		if err := secureDirectory(*securePath, *secureMode); err != nil {
			return fmt.Errorf("secure directory: %w", err)
		}
		return json.NewEncoder(os.Stdout).Encode(map[string]any{"success": true, "secureDirectoryProtocol": 1, "mode": *secureMode})
	}
	if *boundedLogPath != "" {
		return boundedLog(os.Stdin, *boundedLogPath)
	}
	if *version != "auto" && *version != "h2" && *version != "h3" || *timeoutMS <= 0 || *timeoutMS > 60000 {
		return fmt.Errorf("invalid outbound configuration")
	}
	if *dohURL != "" {
		u, e := url.Parse(*dohURL)
		if e != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.Fragment != "" {
			return fmt.Errorf("invalid HTTPS DoH endpoint")
		}
	}
	token := os.Getenv("DANMU_OUTBOUND_TOKEN")
	if len(token) < 32 {
		return fmt.Errorf("missing helper authentication token")
	}
	timeout := time.Duration(*timeoutMS) * time.Millisecond
	transport := newOutboundTransport(newResolver(*dohURL, timeout), *version, timeout)
	defer transport.close()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return fmt.Errorf("cannot bind loopback helper: %w", err)
	}
	defer listener.Close()
	var handler http.Handler = helperHandler(token, transport)
	var githubServer *githubTunnelServer
	if *githubProxy {
		githubServer = newGithubTunnelServer(token, transport.resolver, timeout)
		handler = githubServer
		defer githubServer.close()
	}
	server := &http.Server{Handler: handler, ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 60 * time.Second, MaxHeaderBytes: 16384}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	stdinDone := make(chan error, 1)
	go func() {
		_, readErr := io.Copy(io.Discard, os.Stdin)
		stdinDone <- readErr
		if githubServer != nil {
			githubServer.waitDrained(ctx)
		}
		stop()
	}()
	if githubServer != nil {
		go func() {
			ticker := time.NewTicker(10 * time.Second)
			defer ticker.Stop()
			for {
				select {
				case <-ctx.Done():
					return
				case now := <-ticker.C:
					if githubServer.idle(now, 60*time.Second) {
						stop()
						return
					}
				}
			}
		}()
	}
	closeDone := make(chan struct{})
	go func() {
		defer close(closeDone)
		<-ctx.Done()
		if err := server.Close(); err != nil {
			log.Printf("phase=shutdown message=%q", safeMessage(err, token))
		}
	}()
	if err := json.NewEncoder(os.Stdout).Encode(map[string]any{"ready": true, "appProtocol": protocolVersion, "protocolVersion": protocolVersion, "version": buildVersion, "pid": os.Getpid(), "url": "http://" + listener.Addr().String()}); err != nil {
		stop()
		<-closeDone
		return fmt.Errorf("helper startup output: %w", err)
	}
	err = server.Serve(listener)
	stop()
	<-closeDone
	if err != nil && err != http.ErrServerClosed {
		return fmt.Errorf("helper server: %w", err)
	}
	select {
	case readErr := <-stdinDone:
		if readErr != nil {
			return fmt.Errorf("helper stdin: %w", readErr)
		}
	default:
	}
	return nil
}
