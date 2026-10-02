package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestEmpty204ResponseKeepsRequiredFields(t *testing.T) {
	token := "empty-response-token-01234567890123456789"
	tr := diagnosticTransport(t, "h2", false, nil)
	tr.dial = func(context.Context, string, targetRecords, string) (*session, error) {
		s := stubSession("h2")
		s.rt = roundTripFunc(func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: 204, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(""))}, nil
		})
		return s, nil
	}
	r := httptest.NewRequest("POST", "http://localhost/request", strings.NewReader(`{"url":"https://api.tmdb.org/test","method":"GET","timeoutMs":1000,"headers":[],"body":""}`))
	r.Header.Set("Authorization", "Bearer "+token)
	w := httptest.NewRecorder()
	helperHandler(token, tr).ServeHTTP(w, r)
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(w.Body.Bytes(), &fields); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"success", "status", "protocol", "ech", "headers", "body"} {
		if _, ok := fields[key]; !ok {
			t.Fatalf("required response field missing: %s", key)
		}
	}
	if string(fields["success"]) != "true" || string(fields["status"]) != "204" || string(fields["ech"]) != "false" || string(fields["headers"]) != "[]" || string(fields["body"]) != `""` {
		t.Fatalf("empty response contract changed: %s", w.Body.String())
	}
}
