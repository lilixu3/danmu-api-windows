package main

import (
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
)

func TestRequestDecoderRejectsMalformedProtocolWithoutDispatch(t *testing.T) {
	var calls atomic.Int32
	server, token := proxyFixture(t, func(*http.Request) (*http.Response, error) {
		calls.Add(1)
		return &http.Response{StatusCode: 204, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(""))}, nil
	})
	valid := `{"url":"https://api.tmdb.org/test","method":"GET","headers":[],"body":"","timeoutMs":1000}`
	invalids := []string{
		strings.Replace(valid, `"url":`, `"extra":false,"url":`, 1),
		strings.Replace(valid, `"url":`, `"url":"https://api.tmdb.org/other","url":`, 1),
		strings.Replace(valid, `"url":`, `"u\u0072l":"https://api.tmdb.org/other","url":`, 1),
		strings.Replace(valid, `"headers":[],`, "", 1), strings.Replace(valid, `"body":"",`, "", 1),
		strings.Replace(valid, `"headers":[]`, `"headers":null`, 1), strings.Replace(valid, `"body":""`, `"body":null`, 1),
		strings.Replace(valid, `"url":"https://api.tmdb.org/test"`, `"url":null`, 1), strings.Replace(valid, `"method":"GET"`, `"method":null`, 1),
		strings.Replace(valid, `"timeoutMs":1000`, `"timeoutMs":null`, 1), strings.Replace(valid, `"timeoutMs":1000`, `"timeoutMs":1.0`, 1),
		strings.Replace(valid, `"headers":[]`, `"headers":[["x","y","z"]]`, 1), strings.Replace(valid, `"headers":[]`, `"headers":[["x"]]`, 1),
		strings.Replace(valid, `"headers":[]`, `"headers":[[null,"y"]]`, 1), strings.Replace(valid, `"headers":[]`, `"headers":[["x",42]]`, 1),
		strings.Replace(valid, `"headers":[]`, `"headers":[["bad header","y"]]`, 1), strings.Replace(valid, `"headers":[]`, `"headers":[["x","bad\r\nheader"]]`, 1),
		valid + ` {}`, `null`, `[]`,
		strings.Replace(valid, `"headers":[]`, `"headers":[`+strings.TrimSuffix(strings.Repeat(`["x","y"],`, 4097), ",")+`]`, 1),
	}
	for _, input := range invalids {
		request, err := http.NewRequest("POST", server.URL+"/request", strings.NewReader(input))
		if err != nil {
			t.Fatal(err)
		}
		request.Header.Set("Authorization", "Bearer "+token)
		response, err := server.Client().Do(request)
		if err != nil {
			t.Fatal(err)
		}
		body, _ := io.ReadAll(response.Body)
		response.Body.Close()
		if response.StatusCode != 400 {
			t.Fatalf("invalid protocol accepted: status=%d response=%s", response.StatusCode, body)
		}
	}
	if calls.Load() != 0 {
		t.Fatal("invalid input dispatched business requests:", calls.Load())
	}
	request, _ := http.NewRequest("POST", server.URL+"/request", strings.NewReader(valid))
	request.Header.Set("Authorization", "Bearer "+token)
	response, err := server.Client().Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 200 || calls.Load() != 1 {
		t.Fatalf("valid explicit empty fields rejected: status=%d calls=%d", response.StatusCode, calls.Load())
	}
}
