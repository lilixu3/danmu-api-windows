package main

import (
	"encoding/json"
	"fmt"
	"io"
	"strconv"

	"golang.org/x/net/http/httpguts"
)

// Decode tokens before they can be collapsed into a map/struct: even escaped
// duplicate names, null strings and overlong fixed-array tuples are rejected.
func decodeHelperRequest(reader io.Reader) (helperRequest, error) {
	var input helperRequest
	decoder := json.NewDecoder(reader)
	decoder.UseNumber()
	expect := func(want json.Delim) error {
		token, err := decoder.Token()
		if err != nil {
			return err
		}
		if token != want {
			return fmt.Errorf("invalid request JSON structure")
		}
		return nil
	}
	stringValue := func() (string, error) {
		token, err := decoder.Token()
		if err != nil {
			return "", err
		}
		value, ok := token.(string)
		if !ok {
			return "", fmt.Errorf("request field must be a string")
		}
		return value, nil
	}
	if err := expect('{'); err != nil {
		return input, err
	}
	seen := make(map[string]bool)
	for decoder.More() {
		key, err := stringValue()
		if err != nil {
			return input, err
		}
		if seen[key] {
			return input, fmt.Errorf("duplicate request field")
		}
		seen[key] = true
		switch key {
		case "url":
			input.URL, err = stringValue()
		case "method":
			input.Method, err = stringValue()
		case "body":
			input.Body, err = stringValue()
		case "timeoutMs":
			var token json.Token
			token, err = decoder.Token()
			if err == nil {
				value, ok := token.(json.Number)
				if !ok {
					err = fmt.Errorf("timeoutMs must be an integer")
				} else {
					input.TimeoutMS, err = strconv.ParseInt(string(value), 10, 64)
					if err != nil {
						err = fmt.Errorf("timeoutMs must be an integer")
					}
				}
			}
		case "headers":
			err = expect('[')
			input.Headers = make([][2]string, 0)
			for err == nil && decoder.More() {
				if len(input.Headers) >= 4096 {
					err = fmt.Errorf("too many request headers")
					break
				}
				var header [2]string
				if err = expect('['); err != nil {
					break
				}
				if header[0], err = stringValue(); err != nil {
					break
				}
				if header[1], err = stringValue(); err != nil {
					break
				}
				if err = expect(']'); err != nil {
					break
				}
				if !httpguts.ValidHeaderFieldName(header[0]) || !httpguts.ValidHeaderFieldValue(header[1]) {
					err = fmt.Errorf("invalid request header")
					break
				}
				input.Headers = append(input.Headers, header)
			}
			if err == nil {
				err = expect(']')
			}
		default:
			return input, fmt.Errorf("unknown request field")
		}
		if err != nil {
			return input, err
		}
	}
	if err := expect('}'); err != nil {
		return input, err
	}
	for _, key := range []string{"url", "method", "headers", "body", "timeoutMs"} {
		if !seen[key] {
			return input, fmt.Errorf("missing request field")
		}
	}
	if _, err := decoder.Token(); err != io.EOF {
		return input, fmt.Errorf("exactly one JSON request required")
	}
	return input, nil
}
