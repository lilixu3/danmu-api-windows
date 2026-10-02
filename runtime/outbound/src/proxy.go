package main

import (
	"bytes"
	"context"
	"crypto/subtle"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const internalPrefix = "x-danmu-outbound-"

func hopHeaders(h http.Header) map[string]bool {
	skip := map[string]bool{"connection": true, "proxy-connection": true, "proxy-authorization": true, "keep-alive": true, "transfer-encoding": true, "upgrade": true, "te": true, "trailer": true}
	for _, value := range h.Values("Connection") {
		for _, name := range strings.Split(value, ",") {
			skip[strings.ToLower(strings.TrimSpace(name))] = true
		}
	}
	return skip
}
func proxyFailure(w http.ResponseWriter, status int, result helperResponse) {
	w.Header().Set("X-Danmu-Outbound-Error", "1")
	w.Header().Set("X-Danmu-Outbound-Phase", result.Phase)
	w.Header().Set("X-Danmu-Outbound-Code", result.ErrorCode)
	respond(w, status, result)
}
func proxyError(w http.ResponseWriter, status int, code, message string) {
	phase := "validation"
	if status == 401 {
		phase = "authentication"
	}
	if status == 403 {
		phase = "policy"
	}
	proxyFailure(w, status, invalid(phase, code, message))
}

// App-owned HTTP adapter. It sends exactly one business request and never follows
// redirects; the caller retains fetch/ClientRequest redirect and cancellation semantics.
func proxyHandler(token string, transport *outboundTransport) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, local *http.Request) {
		if subtle.ConstantTimeCompare([]byte(local.Header.Get("X-Danmu-Outbound-Token")), []byte(token)) != 1 {
			proxyError(w, 401, "unauthorized", "helper authentication failed")
			return
		}
		target, err := url.Parse(local.Header.Get("X-Danmu-Outbound-Target"))
		if err != nil || !allowedTarget(target) {
			proxyError(w, 403, "target_not_allowed", "target not allowed")
			return
		}
		switch local.Method {
		case "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS":
		default:
			proxyError(w, 405, "method_not_allowed", "method not allowed")
			return
		}
		budget, err := strconv.Atoi(local.Header.Get("X-Danmu-Outbound-Timeout"))
		if err != nil || budget < 1 || budget > 3600000 {
			proxyError(w, 400, "invalid_deadline", "invalid deadline")
			return
		}
		ctx, cancel := context.WithTimeout(local.Context(), time.Duration(budget)*time.Millisecond)
		defer cancel()
		// Buffer the small source API body before sending it. Exceeding the limit or
		// cancellation cannot leave a partially sent non-idempotent upstream request.
		body, err := readProxyBody(ctx, w, local)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			result := failureResult("request body", "body_read_failed", err, requestSecrets(token, local)...)
			logFailure(target.Hostname(), result)
			proxyFailure(w, 413, result)
			return
		}
		request, err := http.NewRequestWithContext(ctx, local.Method, target.String(), bytes.NewReader(body))
		if err != nil {
			proxyError(w, 400, "invalid_request", "invalid upstream request")
			return
		}
		skip := hopHeaders(local.Header)
		for name, values := range local.Header {
			lower := strings.ToLower(name)
			if skip[lower] || strings.HasPrefix(lower, internalPrefix) || lower == "host" || lower == "content-length" {
				continue
			}
			for _, value := range values {
				request.Header.Add(name, value)
			}
		}
		start := time.Now()
		secrets := append(requestSecrets(token, request), string(body))
		response, session, err := transport.roundTrip(request)
		if err != nil {
			result := transportFailure(ctx, session, err, secrets...)
			addResultMetadata(&result, target.Hostname(), start, session)
			logFailure(target.Hostname(), result)
			proxyFailure(w, 502, result)
			return
		}
		defer logClose(target.Hostname(), response.Body, secrets...)
		skip = hopHeaders(response.Header)
		for name, values := range response.Header {
			lower := strings.ToLower(name)
			if skip[lower] || strings.HasPrefix(lower, internalPrefix) {
				continue
			}
			for _, value := range values {
				w.Header().Add(name, value)
			}
		}
		// Reserved internal metadata cannot be supplied or spoofed by upstream.
		w.Header().Set("X-Danmu-Outbound-Protocol", session.protocol)
		w.Header().Set("X-Danmu-Outbound-Ech", strconv.FormatBool(session.ech))
		w.WriteHeader(response.StatusCode)
		// Flush headers and every upstream chunk, without a detached pump or queue.
		// Each Write/Flush blocks on downstream backpressure and propagates failure.
		controller := http.NewResponseController(w)
		err = controller.Flush()
		if err == nil && local.Method != "HEAD" {
			_, err = io.Copy(flushingWriter{w, controller}, response.Body)
		}
		if err != nil {
			result := transportFailure(ctx, session, err, secrets...)
			result.Phase = "response stream"
			logFailure(target.Hostname(), result)
			// Headers are already sent. Abort the stream and retain diagnostic in stderr;
			// sending a JSON error here would corrupt the upstream body.
			panic(http.ErrAbortHandler)
		}
		log.Printf("host=%s protocol=%s ech=%t status=%d duration=%s", target.Hostname(), session.protocol, session.ech, response.StatusCode, time.Since(start).Round(time.Millisecond))
	})
}

type flushingWriter struct {
	writer     http.ResponseWriter
	controller *http.ResponseController
}

func (w flushingWriter) Write(p []byte) (int, error) {
	n, err := w.writer.Write(p)
	if err == nil {
		err = w.controller.Flush()
	}
	return n, err
}
func readProxyBody(ctx context.Context, w http.ResponseWriter, r *http.Request) ([]byte, error) {
	if r.ContentLength > maxBody {
		return nil, fmt.Errorf("body too large")
	}
	if r.Body == nil || r.Body == http.NoBody {
		return nil, ctx.Err()
	}
	// Cancelling a context alone cannot interrupt the server's socket Body.Read.
	// The real connection deadline covers uploads too; cancellation advances it.
	controller := http.NewResponseController(w)
	deadline, ok := ctx.Deadline()
	if !ok {
		return nil, fmt.Errorf("upload has no request deadline")
	}
	if err := controller.SetReadDeadline(deadline); err != nil {
		return nil, fmt.Errorf("upload deadline: %w", err)
	}
	complete := false
	interrupted := make(chan struct{})
	stop := context.AfterFunc(ctx, func() {
		defer close(interrupted)
		if err := controller.SetReadDeadline(time.Now()); err != nil {
			log.Printf("phase=upload_cancel errorCode=deadline_failed message=%q", safeMessage(err))
		}
	})
	defer func() {
		if !stop() {
			<-interrupted
		}
		// Do not reset an expired deadline on an incomplete body: net/http's
		// response writer would otherwise drain the remaining upload indefinitely.
		if complete {
			if err := controller.SetReadDeadline(time.Time{}); err != nil {
				log.Printf("phase=upload_cleanup errorCode=deadline_failed message=%q", safeMessage(err))
			}
		}
	}()
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBody))
	complete = err == nil && ctx.Err() == nil
	if !complete {
		w.Header().Set("Connection", "close")
		if deadlineErr := controller.SetReadDeadline(time.Now()); deadlineErr != nil {
			return nil, fmt.Errorf("upload deadline cleanup: %w (read error: %v)", deadlineErr, err)
		}
	}
	return body, err
}
