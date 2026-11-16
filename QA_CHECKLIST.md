# Phase 9 QA Checklist

## Automated verification

- [x] Full solution build
- [x] Core and HTTP integration tests
- [x] HTTP/HTTPS URL validation tests
- [x] Browser manifest JSON validation
- [x] Self-contained browser-host publish
- [x] Native-host message-size and URL validation implemented

## Security review

- [x] No TLS certificate validation bypass
- [x] Only HTTP and HTTPS URLs accepted
- [x] Browser host rejects invalid schemes and oversized messages
- [x] Browser host does not execute commands or accept local paths
- [x] Downloaded files are never automatically executed
- [x] Temporary files use `.part` suffix before finalization
- [x] Filenames are sanitized before use

## Manual Windows verification still required

- [ ] Test pause/resume against a slow server
- [ ] Test segmented download against a range-supporting server
- [ ] Test Chrome, Edge, Brave, and Firefox native messaging end to end
- [ ] Test tray restore and exit behavior
- [ ] Test clean install/uninstall on a separate Windows environment
