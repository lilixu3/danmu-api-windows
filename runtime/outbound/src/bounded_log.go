package main

import (
	"errors"
	"fmt"
	"io"
	"os"
)

const logLimit = 1024 * 1024
const logKeep = 256 * 1024

// Event-driven log sink: no polling, no network, no whole-file read, EOF exits.
func boundedLog(input io.Reader, filename string) (result error) {
	// Windows O_APPEND opens FILE_APPEND_DATA without the truncate access needed
	// by SetEndOfFile. This sink owns its writer: seek before each append instead.
	file, err := os.OpenFile(filename, os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return err
	}
	defer func() {
		if err := file.Close(); err != nil {
			result = errors.Join(result, err)
		}
	}()
	info, err := file.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("invalid log file")
	}
	appendBytes := func(data []byte) error {
		if _, err := file.Seek(0, io.SeekEnd); err != nil {
			return err
		}
		_, err := file.Write(data)
		return err
	}
	tail := make([]byte, logKeep)
	rotate := func() error {
		info, err := file.Stat()
		if err != nil {
			return err
		}
		if info.Size() <= logLimit {
			return nil
		}
		n, err := file.ReadAt(tail, info.Size()-logKeep)
		if err != nil && err != io.EOF {
			return err
		}
		// Retain the inode so app clear/read and the writer agree on the same file.
		if err = file.Truncate(0); err != nil {
			return err
		}
		return appendBytes(tail[:n])
	}
	if err = rotate(); err != nil {
		return err
	}
	buffer := make([]byte, 8192)
	for {
		n, readErr := input.Read(buffer)
		if n > 0 {
			if err = appendBytes(buffer[:n]); err != nil {
				return err
			}
			if err = rotate(); err != nil {
				return err
			}
		}
		if readErr == io.EOF {
			return nil
		}
		if readErr != nil {
			return readErr
		}
	}
}
