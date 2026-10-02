package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"log"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

// Build injects the app helper version and an input fingerprint; no VCS probe.
var buildVersion = "1.0.1+6004732"

const protocolVersion = 1

var diagnosticURL = regexp.MustCompile(`(?i)https?://[^\s"'<>]+`)

// Error strings from net/http can contain a URL, including signed query values.
// Keep resolver/target authority and the original DNS/TLS/QUIC reason, never paths,
// userinfo, query, request headers, authentication or business body.
func safeMessage(err error, secrets ...string) string {
	if err == nil {
		return ""
	}
	message := err.Error()
	message = diagnosticURL.ReplaceAllStringFunc(message, func(raw string) string {
		u, parseErr := url.Parse(raw)
		if parseErr != nil || u.Hostname() == "" {
			return "[redacted URL]"
		}
		return u.Scheme + "://" + u.Host
	})
	for _, secret := range secrets {
		if secret != "" {
			message = strings.ReplaceAll(message, secret, "[redacted]")
		}
	}
	message = strings.Join(strings.Fields(message), " ")
	if len(message) > 4096 {
		message = message[:4096] + " [truncated]"
	}
	return message
}
func requestSecrets(token string, r *http.Request) []string {
	secrets := []string{token}
	if r == nil {
		return secrets
	}
	for name, values := range r.Header {
		upper := strings.ToUpper(name)
		if strings.Contains(upper, "TOKEN") || strings.Contains(upper, "COOKIE") || strings.Contains(upper, "AUTHORIZATION") || strings.Contains(upper, "KEY") {
			secrets = append(secrets, values...)
			for _, value := range values {
				secrets = append(secrets, strings.TrimPrefix(value, "Bearer "))
			}
		}
	}
	if r.URL != nil {
		secrets = append(secrets, r.URL.RawQuery)
		for name, values := range r.URL.Query() {
			upper := strings.ToUpper(name)
			// URLs themselves are removed above. Only credential query values need
			// standalone replacement: a non-secret limit=1 must not erase IP digits.
			if strings.Contains(upper, "TOKEN") || strings.Contains(upper, "COOKIE") || strings.Contains(upper, "KEY") || strings.Contains(upper, "SECRET") || strings.Contains(upper, "SIGNATURE") || strings.Contains(upper, "PASSWORD") || strings.Contains(upper, "AUTH") {
				secrets = append(secrets, values...)
			}
		}
	}
	return secrets
}
func failureResult(phase, code string, err error, secrets ...string) helperResponse {
	message := safeMessage(err, secrets...)
	return helperResponse{Success: false, Phase: phase, ErrorCode: code, Code: code, Message: message, Error: message}
}
func transportFailure(ctx context.Context, connection *session, err error, secrets ...string) helperResponse {
	phase, code := "connection", "connection_failed"
	var failure *phaseError
	if errors.As(err, &failure) {
		phase = failure.phase
		switch phase {
		case "DNS", "DNS refresh":
			code = "dns_failed"
		case "ECH configuration":
			code = "ech_config_missing"
		case "ECH negotiation":
			code = "ech_rejected"
		case "TLS/QUIC handshake":
			code = "tls_quic_handshake_failed"
		}
	} else if connection != nil {
		phase, code = "request", "request_failed"
	}
	var cert *tls.CertificateVerificationError
	var unknownCA x509.UnknownAuthorityError
	var hostname x509.HostnameError
	if errors.As(err, &cert) || errors.As(err, &unknownCA) || errors.As(err, &hostname) {
		code = "certificate_invalid"
	}
	if errors.Is(err, context.DeadlineExceeded) || errors.Is(ctx.Err(), context.DeadlineExceeded) {
		code = "timeout"
	}
	if errors.Is(ctx.Err(), context.Canceled) {
		code = "cancelled"
	}
	result := failureResult(phase, code, err, secrets...)
	if connection != nil {
		result.Protocol, result.ECH = connection.protocol, connection.ech
	}
	return result
}
func logFailure(host string, result helperResponse) {
	log.Printf("host=%s phase=%q errorCode=%s message=%q", host, result.Phase, result.ErrorCode, result.Message)
}
func logClose(host string, closer interface{ Close() error }, secrets ...string) {
	if err := closer.Close(); err != nil {
		log.Printf("host=%s phase=cleanup errorCode=close_failed message=%q", host, safeMessage(err, secrets...))
	}
}
func addResultMetadata(result *helperResponse, host string, start time.Time, connection *session) {
	result.Host, result.DurationMS = host, time.Since(start).Milliseconds()
	if connection != nil {
		result.Protocol, result.ECH = connection.protocol, connection.ech
	}
}
func invalid(phase, code, message string) helperResponse {
	return failureResult(phase, code, fmt.Errorf("%s", message))
}
