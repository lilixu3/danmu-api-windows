package main

import (
	"errors"
	"net/http"
	"strings"
	"testing"
)

func TestRedactionKeepsNetworkEvidenceWithNonsecretNumericQuery(t *testing.T) {
	req, err := http.NewRequest("GET", "https://api.bangumi.vip/v0/episodes?subject_id=400602&limit=1&token=private-query", nil)
	if err != nil {
		t.Fatal(err)
	}
	message := safeMessage(errors.New(`Post "https://api.bangumi.vip/v0/episodes?subject_id=400602&limit=1&token=private-query": h3 104.21.5.211:443: timeout: no recent network activity`), requestSecrets("private-helper-token", req)...)
	if !strings.Contains(message, "104.21.5.211:443") || strings.Contains(message, "private-query") || strings.Contains(message, "subject_id") || strings.Contains(message, "limit=") {
		t.Fatalf("redaction lost network cause or exposed query: %s", message)
	}
}
