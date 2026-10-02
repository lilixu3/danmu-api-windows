package main

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestProxyDeliversSmallChunkBeforeEOF(t *testing.T) {
	release := make(chan struct{})
	defer close(release)
	server, token := proxyFixture(t, func(*http.Request) (*http.Response, error) {
		reader, writer := io.Pipe()
		go func() {
			defer writer.Close()
			if _, err := io.WriteString(writer, "first"); err != nil {
				return
			}
			<-release
			io.WriteString(writer, "last")
		}()
		return &http.Response{StatusCode: 200, Header: make(http.Header), Body: reader}, nil
	})
	result := make(chan error, 1)
	go func() {
		response, err := server.Client().Do(proxyRequest(t, server.URL, token, "https://api.tmdb.org/stream"))
		if err != nil {
			result <- err
			return
		}
		defer response.Body.Close()
		chunk := make([]byte, 5)
		_, err = io.ReadFull(response.Body, chunk)
		if err == nil && string(chunk) != "first" {
			err = fmt.Errorf("chunk changed: %q", chunk)
		}
		result <- err
	}()
	select {
	case err := <-result:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(time.Second):
		t.Fatal("headers/first small chunk buffered until EOF")
	}
}
func TestProxySlowUploadDeadlineHasZeroDispatch(t *testing.T) {
	var calls atomic.Int32
	server, token := proxyFixture(t, func(*http.Request) (*http.Response, error) {
		calls.Add(1)
		return nil, errors.New("must not dispatch incomplete upload")
	})
	connection, err := net.Dial("tcp", strings.TrimPrefix(server.URL, "http://"))
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	if err = connection.SetDeadline(time.Now().Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	start := time.Now()
	_, err = fmt.Fprintf(connection, "POST /proxy HTTP/1.1\r\nHost: localhost\r\nContent-Length: 100000\r\nX-Danmu-Outbound-Token: %s\r\nX-Danmu-Outbound-Target: https://api.tmdb.org/upload\r\nX-Danmu-Outbound-Timeout: 30\r\n\r\nx", token)
	if err != nil {
		t.Fatal(err)
	}
	response, err := http.ReadResponse(bufio.NewReader(connection), nil)
	if err != nil {
		t.Fatal("upload deadline did not return diagnostic without client EOF:", err)
	}
	defer response.Body.Close()
	if response.Header.Get("X-Danmu-Outbound-Error") != "1" || response.StatusCode < 400 {
		t.Fatal("upload timeout not explicit")
	}
	if elapsed := time.Since(start); elapsed > 500*time.Millisecond {
		t.Fatal("30ms upload budget did not interrupt blocked connection:", elapsed)
	}
	if calls.Load() != 0 {
		t.Fatal("incomplete upload dispatched upstream")
	}
	t.Log("30ms budget interrupted still-open raw upload in", time.Since(start))
}

type flushFailWriter struct{ *httptest.ResponseRecorder }

func (w flushFailWriter) FlushError() error { return errors.New("downstream flush failure proof") }
func TestFlushingWriterPropagatesFlushFailure(t *testing.T) {
	writer := flushFailWriter{httptest.NewRecorder()}
	n, err := (flushingWriter{writer, http.NewResponseController(writer)}).Write([]byte("small"))
	if n != 5 || err == nil || !strings.Contains(err.Error(), "flush failure proof") {
		t.Fatal("flush failure silently discarded:", n, err)
	}
}

type blockedWriter struct{ started, release chan struct{} }

func (w blockedWriter) Header() http.Header { return make(http.Header) }
func (w blockedWriter) WriteHeader(int)     {}
func (w blockedWriter) Write(p []byte) (int, error) {
	close(w.started)
	<-w.release
	return len(p), errors.New("write failure proof")
}
func (w blockedWriter) FlushError() error { return nil }

type countingReader struct{ calls atomic.Int32 }

func (r *countingReader) Read(p []byte) (int, error) { r.calls.Add(1); copy(p, "one"); return 3, nil }
func TestProxyCopyHonorsBackpressureAndWriteFailure(t *testing.T) {
	writer := blockedWriter{make(chan struct{}), make(chan struct{})}
	source := new(countingReader)
	done := make(chan error, 1)
	go func() {
		_, err := io.Copy(flushingWriter{writer, http.NewResponseController(writer)}, source)
		done <- err
	}()
	<-writer.started
	if source.calls.Load() != 1 {
		t.Fatal("upstream read ahead of blocked downstream")
	}
	close(writer.release)
	if err := <-done; err == nil || !strings.Contains(err.Error(), "write failure proof") {
		t.Fatal("write failure lost:", err)
	}
	if source.calls.Load() != 1 {
		t.Fatal("continued reading after write failure")
	}
}
