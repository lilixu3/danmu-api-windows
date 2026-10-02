//go:build !windows

package main

import "fmt"

func secureDirectory(name, mode string) error {
	return fmt.Errorf("Windows secure directory protocol is not supported on this platform")
}
