//go:build windows

package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"golang.org/x/sys/windows"
)

func testDirectoryACL(t *testing.T, name, extra string) {
	t.Helper()
	current, err := currentSecuritySID()
	if err != nil {
		t.Fatal(err)
	}
	sd, err := windows.SecurityDescriptorFromString(fmt.Sprintf("D:P(A;OICI;FA;;;%s)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)%s", current, extra))
	if err != nil {
		t.Fatal(err)
	}
	acl, _, err := sd.DACL()
	if err != nil {
		t.Fatal(err)
	}
	if err = windows.SetNamedSecurityInfo(name, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, acl, nil); err != nil {
		t.Fatal(err)
	}
}
func TestWindowsSecureDirectoryProtectsPendingBeforeWrite(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "outbound")
	if err := os.Mkdir(target, 0700); err != nil {
		t.Fatal(err)
	}
	testDirectoryACL(t, target, "(A;OICI;FRFX;;;BU)")
	if err := secureDirectory(target, "verify"); err == nil {
		t.Fatal("readable private directory accepted")
	}
	if err := secureDirectory(target, "prepare"); err != nil {
		t.Fatal(err)
	}
	current, err := currentSecuritySID()
	if err != nil {
		t.Fatal(err)
	}
	pending := filepath.Join(target, "session.fixture.pending")
	if err = os.WriteFile(pending, []byte("NON_SECRET_PERMISSION_MARKER"), 0600); err != nil {
		t.Fatal(err)
	}
	if err = verifyPrivateFile(pending, current); err != nil {
		t.Fatal("pending inherited unsafe ACL:", err)
	}
	sd, err := windows.GetNamedSecurityInfo(pending, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	t.Log("native pending DACL:", sd.String())
	if err = os.Rename(pending, filepath.Join(target, "session.json")); err != nil {
		t.Fatal(err)
	}
	if err = secureDirectory(target, "verify"); err != nil {
		t.Fatal(err)
	}
}
func TestWindowsSecureDirectoryRejectsReplaceableAncestor(t *testing.T) {
	root := t.TempDir()
	parent := filepath.Join(root, "shared")
	target := filepath.Join(parent, "outbound")
	if err := os.MkdirAll(target, 0700); err != nil {
		t.Fatal(err)
	}
	current, err := currentSecuritySID()
	if err != nil {
		t.Fatal(err)
	}
	if err = setPrivateACL(target, current, true); err != nil {
		t.Fatal(err)
	}
	testDirectoryACL(t, parent, "(A;;0x00000040;;;AU)") // DELETE_CHILD on parent only.
	if err = secureDirectory(target, "prepare"); err == nil || !strings.Contains(err.Error(), "ancestor") {
		t.Fatal("protected child did not reject replaceable ancestor:", err)
	}
	if entries, e := os.ReadDir(target); e != nil || len(entries) != 0 {
		t.Fatal("rejected path received files")
	}
	t.Log("native protected child + shared DELETE_CHILD ancestor explicitly rejected")
}
func TestWindowsSecureDirectoryRejectsWritableExecutable(t *testing.T) {
	file := filepath.Join(t.TempDir(), "untrusted.exe")
	if err := os.WriteFile(file, []byte("not executed"), 0600); err != nil {
		t.Fatal(err)
	}
	testDirectoryACL(t, file, "(A;;FW;;;AU)")
	current, err := currentSecuritySID()
	if err != nil {
		t.Fatal(err)
	}
	if err = verifyPathACL(file, current, false, true); err == nil {
		t.Fatal("other-user writable executable accepted")
	}
	t.Log("native other-user writable executable explicitly rejected before execution")
}
func TestWindowsSecureDirectoryAllowsReadonlyAncestors(t *testing.T) {
	parent := filepath.Join(t.TempDir(), "readonly-shared")
	target := filepath.Join(parent, "outbound")
	if err := os.MkdirAll(target, 0700); err != nil {
		t.Fatal(err)
	}
	testDirectoryACL(t, parent, "(A;;FRFX;;;BU)")
	current, err := currentSecuritySID()
	if err != nil {
		t.Fatal(err)
	}
	if err = setPrivateACL(target, current, true); err != nil {
		t.Fatal(err)
	}
	if err = secureDirectory(target, "verify"); err != nil {
		t.Fatal("read/traverse ancestor rejected:", err)
	}
}
