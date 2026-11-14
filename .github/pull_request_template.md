## Summary / 요약

<!-- What does this change and why? / 무엇을, 왜 바꾸나요? -->

## Checks / 확인

- [ ] `.\build.ps1 test` passes and the build has 0 warnings / 테스트 통과, 빌드 경고 0개
- [ ] New logic has tests / 새 로직에 테스트 추가
- [ ] New UI text is wrapped (`L.T` / `L.F` / `{l:Tr}`) and translated in `Lang\en\*.json` / 새 화면 문구를 감싸고 영어 번역 추가
- [ ] Remote actions from the Claude panel go through approval + audit log; no secrets in results or logs / Claude 패널의 원격 동작은 승인·감사 로그, 비밀 값 노출 없음
- [ ] `README.md` and `README.ko.md` (and `docs/guide*.md`) updated if a feature changed / 기능이 바뀌면 두 README(와 가이드) 수정
- [ ] Tested with `NUBIS_DATA_DIR` pointing to a test folder, not real data / 실제 데이터가 아닌 테스트 폴더로 확인

## Not verified / 확인하지 못한 것

<!-- e.g. WinRM against a real server, RD Gateway, real ssh-agent / 예: 실제 WinRM 서버, RD 게이트웨이, 실제 ssh-agent -->
