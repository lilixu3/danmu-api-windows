//go:build windows

package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

const trustedInstallerSID = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"

func currentSecuritySID() (string, error) {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return "", err
	}
	return user.User.Sid.String(), nil
}
func trustedSID(sid, current string) bool {
	return sid == current || sid == "S-1-5-18" || sid == "S-1-5-32-544" || sid == trustedInstallerSID
}

// Never infer trust from the child alone. DELETE_CHILD on an ancestor can
// replace even a protected child; WRITE_DAC/OWNER or DELETE is also unsafe.
// Harmless read/traverse and creation-only rights on distant ancestors remain
// allowed. Unsupported ACE forms are rejected, not guessed to be safe.
func verifyPathACL(name, current string, private, fullWrite bool) error {
	ptr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return err
	}
	attrs, err := windows.GetFileAttributes(ptr)
	if err != nil {
		return err
	}
	if attrs&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
		return fmt.Errorf("reparse path is not trusted")
	}
	sd, err := windows.GetNamedSecurityInfo(name, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		return err
	}
	owner, _, err := sd.Owner()
	if err != nil || owner == nil {
		return fmt.Errorf("cannot verify path owner")
	}
	if !trustedSID(owner.String(), current) {
		return fmt.Errorf("untrusted path owner")
	}
	acl, _, err := sd.DACL()
	if err != nil || acl == nil {
		return fmt.Errorf("missing or permissive path DACL")
	}
	control, _, err := sd.Control()
	if err != nil {
		return err
	}
	if private && control&windows.SE_DACL_PROTECTED == 0 {
		return fmt.Errorf("private DACL is not protected")
	}
	danger := uint32(windows.DELETE | windows.WRITE_DAC | windows.WRITE_OWNER | 0x40 /* FILE_DELETE_CHILD */ | windows.GENERIC_ALL | windows.GENERIC_WRITE)
	if fullWrite {
		danger |= windows.FILE_WRITE_DATA | windows.FILE_APPEND_DATA | windows.FILE_WRITE_EA | windows.FILE_WRITE_ATTRIBUTES
	}
	currentFull := false
	for i := uint32(0); i < uint32(acl.AceCount); i++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(acl, i, &ace); err != nil {
			return err
		}
		if ace.Header.AceFlags&windows.INHERIT_ONLY_ACE != 0 {
			continue
		}
		if ace.Header.AceType == windows.ACCESS_DENIED_ACE_TYPE {
			continue
		}
		if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE {
			return fmt.Errorf("unsupported path ACE")
		}
		sid := (*windows.SID)(unsafe.Pointer(&ace.SidStart)).String()
		mask := uint32(ace.Mask)
		if sid == current && mask&0x1f01ff == 0x1f01ff /* FILE_ALL_ACCESS */ {
			currentFull = true
		}
		if !trustedSID(sid, current) && (private && mask != 0 || mask&danger != 0) {
			return fmt.Errorf("other-user access makes path unsafe")
		}
	}
	if private && !currentFull {
		return fmt.Errorf("private DACL does not grant current user full access")
	}
	return nil
}
func verifyAncestors(name, current string) error {
	for p := filepath.Dir(name); ; p = filepath.Dir(p) {
		if err := verifyPathACL(p, current, false, false); err != nil {
			return fmt.Errorf("unsafe ancestor: %w", err)
		}
		if filepath.Dir(p) == p {
			break
		}
	}
	return nil
}
func setPrivateACL(name, current string, directory bool) error {
	flags := ""
	if directory {
		flags = "OICI"
	}
	sd, err := windows.SecurityDescriptorFromString(fmt.Sprintf("D:P(A;%s;FA;;;%s)(A;%s;FA;;;SY)(A;%s;FA;;;BA)", flags, current, flags, flags))
	if err != nil {
		return err
	}
	acl, _, err := sd.DACL()
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(name, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, acl, nil)
}
func secureDirectory(name, mode string) error {
	if mode != "prepare" && mode != "verify" {
		return fmt.Errorf("secure directory mode must be prepare or verify")
	}
	if !filepath.IsAbs(name) || len(filepath.VolumeName(name)) != 2 || strings.HasPrefix(name, `\\`) {
		return fmt.Errorf("secure directory requires absolute local drive path")
	}
	name = filepath.Clean(name)
	current, err := currentSecuritySID()
	if err != nil {
		return err
	}
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	if err = verifyPathACL(exe, current, false, true); err != nil {
		return fmt.Errorf("unsafe executable: %w", err)
	}
	if err = verifyPathACL(filepath.Dir(exe), current, false, true); err != nil {
		return fmt.Errorf("unsafe executable directory: %w", err)
	}
	if err = verifyAncestors(exe, current); err != nil {
		return err
	}
	// All existing ancestors must be non-replaceable before we create anything.
	if err = verifyAncestors(name, current); err != nil {
		return err
	}
	if mode == "prepare" {
		if _, err = os.Stat(name); os.IsNotExist(err) {
			sd, sdErr := windows.SecurityDescriptorFromString(fmt.Sprintf("D:P(A;OICI;FA;;;%s)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", current))
			if sdErr != nil {
				return sdErr
			}
			ptr, e := windows.UTF16PtrFromString(name)
			if e != nil {
				return e
			}
			err = windows.CreateDirectory(ptr, &windows.SecurityAttributes{Length: uint32(unsafe.Sizeof(windows.SecurityAttributes{})), SecurityDescriptor: sd})
			if err != nil {
				return err
			}
		} else if err != nil {
			return err
		}
		if err = verifyPathACL(name, current, false, false); err != nil {
			return err
		}
		// No token bytes exist yet; apply protected inheritance before pending writes.
		if err = setPrivateACL(name, current, true); err != nil {
			return err
		}
	}
	if err = verifyPathACL(name, current, true, true); err != nil {
		return err
	}
	entries, err := os.ReadDir(name)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		file := filepath.Join(name, entry.Name())
		if entry.IsDir() {
			return fmt.Errorf("private directory contains unexpected child directory")
		}
		if mode == "prepare" {
			if err = verifyPathACL(file, current, false, false); err != nil {
				return err
			}
			if err = setPrivateACL(file, current, false); err != nil {
				return err
			}
		}
		// Files inherit the owner-only ACL; protection is required only on the directory.
		if err = verifyPrivateFile(file, current); err != nil {
			return err
		}
	}
	return nil
}
func verifyPrivateFile(name, current string) error {
	// An inherited DACL is safe here because the verified protected parent controls it.
	if err := verifyPathACL(name, current, false, true); err != nil {
		return err
	}
	sd, err := windows.GetNamedSecurityInfo(name, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		return err
	}
	acl, _, err := sd.DACL()
	if err != nil || acl == nil {
		return fmt.Errorf("private file DACL unavailable")
	}
	for i := uint32(0); i < uint32(acl.AceCount); i++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err = windows.GetAce(acl, i, &ace); err != nil {
			return err
		}
		if ace.Header.AceFlags&windows.INHERIT_ONLY_ACE != 0 || ace.Header.AceType == windows.ACCESS_DENIED_ACE_TYPE {
			continue
		}
		if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE {
			return fmt.Errorf("unsupported private file ACE")
		}
		if !trustedSID((*windows.SID)(unsafe.Pointer(&ace.SidStart)).String(), current) && ace.Mask != 0 {
			return fmt.Errorf("private file permits other-user access")
		}
	}
	return nil
}
