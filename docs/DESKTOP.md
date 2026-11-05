# ReviewGate 데스크톱 앱 (v0.3)

API 키 등록, 모델 선택, 간이 테스트, 실행 기록을 한 화면에서 다루는 Windows 앱입니다. CLI와 같은 엔진(계획·예산·grant·장부·근거 검사)을 그대로 쓰고, 상태 폴더도 CLI 기본값(`%LOCALAPPDATA%\ReviewGate`)을 공유합니다.

## 실행

```powershell
dotnet publish src/ReviewGate.Desktop -c Release -o .work/desktop
.work\desktop\ReviewGate.Desktop.exe
```

.NET 10 Desktop Runtime이 필요합니다(framework-dependent). `profiles/`가 실행 파일 옆에 함께 복사됩니다.

## 탭

| 탭 | 하는 일 |
|---|---|
| API 키 | 제공자마다 이름 붙인 키 목록(값은 표시하지 않음), 사용할 키 선택, 키 추가·삭제, 대표 모델로 샘플 코드 1건 연결 테스트. CLI 검수자는 설치 위치와 연결 테스트만 표시 |
| 모델 | Google 무료 티어 사용 여부, 제공자별 **대표 모델**과 **서브 모델**(대표가 응답하지 않을 때), 모델·가격 목록, 새 모델 추가(무료 또는 직접 입력한 가격) |
| 간이 테스트 | 검수자 1명(단일) 또는 2명(독립 검수)을 제공자 단위로 선택(사용 중 키·대표·서브 모델), 검수 유형, 지시, 붙여 넣은 텍스트나 파일·폴더 → 미리보기 → 승인 → 결과(지적·근거 확인·비용·보고서) |
| 실행 기록 | 최근 실행, 결과 다시 보기, 비용 미확정 정리(청구 없음 / 예약액을 비용으로) |

## 키 목록

- 키는 `ReviewGate/live/<제공자>/key/<id>` 항목으로 Windows 자격 증명 관리자에 저장하고, 표시 이름만 사용자 이름 칸에 둡니다. 어떤 키를 쓸지는 `ReviewGate/live/<제공자>/active` 항목(키 id만 저장)으로 정합니다. CLI도 같은 키 목록을 씁니다(`credential status|set --name|activate --key-id|delete --key-id`).
- 새 키를 추가하면 그 키를 바로 사용합니다. 키가 하나만 남으면 그 키를 사용합니다.
- v0.2에서 저장한 키(`ReviewGate/live/<제공자>`)는 "기존 키"로 목록에 보이고 그대로 쓸 수 있습니다. 앱이 몰래 옮기거나 지우지 않습니다.

## 대표·서브 모델

- 서브 모델은 대표 모델이 **응답하지 않을 때**만 씁니다. 해당하는 경우는 시간 초과, 연결 끊김, 서버 혼잡(5xx), 호출 한도(429), 응답 크기 초과입니다. 키 오류(401/403), 모델이 답했지만 잘림·거부·형식 오류, 사용자 취소는 서브로 넘어가지 않습니다.
- 서브 요청은 계획에 미리 들어가고, 전송 승인 창에 "응답이 없으면 서브: …"로 함께 표시됩니다. 예약 상한도 서브까지 포함한 최악의 경우입니다. 결과에는 실제로 답한 모델이 남습니다.
- 대표·서브·두 번째 검수자는 모두 서로 다른 모델이어야 합니다. 독립 검수의 독립성은 대표 모델 기준으로 판정하고, 서브 모델은 provenance에 따로 기록합니다.
- 대표 모델의 비용이 미확정이면 서브가 답해도 그 보류는 남고, 실행 상태는 "비용 미확정"이 됩니다. 단, 가격표에서 모든 단가가 0인 모델(무료 티어)은 어떤 사용량이든 0원이므로 자동으로 0원 정산합니다.

## CLI 검수자 (Claude Code · Codex · Antigravity)

API 검수자와 같은 계획·grant·장부·근거 검사를 거치며, API 모델과 섞어 쓸 수 있습니다(예: 대표 OpenAI + 독립 검수 Claude Code, 또는 Google 대표의 서브로 Codex).

| 제공자 id | 실행 파일 | 호출 방식 |
|---|---|---|
| `claude-cli` | `claude.exe` | `-p --output-format json --json-schema <스키마> --tools "" --no-session-persistence --system-prompt <지시>`, 본문은 표준 입력 |
| `codex-cli` | `codex.exe` | `exec - --skip-git-repo-check --ephemeral --json --output-schema schema.json -o last-message.json`, 지시+본문은 표준 입력 |
| `agy-cli` | `agy.exe` | 표준 입력을 읽지 않으므로 요청을 `review-request.md`에 쓰고 `-p=<그 파일을 읽으라는 지시> --output-format json --json-schema schema.json` |

- 각 CLI의 **로그인한 구독 계정**으로 처리됩니다. 키는 필요 없고, 데이터 학습·보관은 그 계정 설정을 따릅니다. 전송 승인 창에 이 경고가 함께 표시됩니다.
- 가격표에는 단가 0, tier `subscription`으로 들어 있습니다. 예약 상한은 0이고, 장부에는 CLI가 보고한 토큰 수만 남습니다(구독 사용량은 각 서비스에서 확인).
- 실행 파일은 PATH와 표준 설치 폴더(`~\.local\bin`, `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`, `%LOCALAPPDATA%\agy\bin`)에서 `.exe`만 찾습니다. `.cmd`/`.ps1` 래퍼는 쓰지 않습니다.
- 사용자 결정에 따라 **샌드박스 없이** 요청마다 새 임시 폴더(`%TEMP%\ReviewGate-cli\<guid>`)에서 실행하고, 끝나면 폴더를 지웁니다. Claude Code는 도구를 모두 끈 상태(`--tools ""`)로 실행합니다. Codex·Antigravity는 도구 사용을 막는 옵션을 주지 않습니다.
- 모델은 각 CLI의 기본 모델입니다(`model_id`가 `default:`로 시작하면 모델 인자를 넘기지 않음). 그래서 같은 모델 판정은 `canonical_model_key` 기준이며, CLI 기본 모델과 같은 API 모델을 함께 고르면 걸러지지 않을 수 있습니다.
- 시간 제한(기본 900초)을 넘기거나 사용자가 취소하면 프로세스 트리를 종료합니다. 출력 8MB 초과, 해석할 수 없는 출력은 "응답 미확인"으로 남습니다(CLI가 이미 모델을 호출했을 수 있음). 단가 0이므로 0원으로 자동 정산됩니다.
- 스키마를 따르지 않은 답(예: 성공이지만 `structured_output`이 없음)은 빈 검수로 치지 않고 형식 오류로 처리합니다.
- Gemini CLI는 쓰지 않습니다(Antigravity CLI로 대체).

## 안전 장치

- 키는 Windows 자격 증명 관리자에 저장하고 화면·결과·로그에 다시 표시하지 않습니다.
- 보내기 전에 전송 대상(제공자·모델·주소), 파일 수와 크기, 예약 상한, 무료 티어 데이터 사용 경고, 제외된 파일을 확인 창으로 보여 줍니다. 확인하면 그 실행 한 번에만 유효한 grant를 발급합니다(`desktop_confirm` 채널). CLI처럼 이 확인도 사람이 승인했다는 증명은 아닙니다.
- 간이 테스트는 "외부 제공자에게 보내도 되는 비민감 자료" 확인란을 선택해야 실행됩니다.
- 붙여 넣은 텍스트는 임시 폴더(`%TEMP%\ReviewGate-quick`)에 파일로 고정한 뒤 검수합니다.
- 앱이 실행하는 외부 프로그램은 CLI 검수자로 고른 `claude.exe`/`codex.exe`/`agy.exe`뿐이며, 프로세스 실행 코드는 `src/ReviewGate.Transport.Cli`에만 있습니다(소스 검사 테스트로 강제). 보고서는 앱 안에서 텍스트로 보여 줍니다.
- 사용자가 추가한 모델의 가격은 입력한 값이며, 제공자 가격표로 검증한 값이 아닙니다.

## 한계

- 화면 자체(WPF)는 자동 테스트하지 않습니다. 화면이 호출하는 `DesktopService`는 `tests/ReviewGate.AppTests`에서 가짜 키 저장소와 mock HTTP로 검사합니다.
- CLI 검수자는 실제 프로세스를 띄우는 부분을 자동 테스트하지 않습니다. 각 CLI의 출력 정규화(`CliTransport.FromClaude/FromCodex/FromAgy`)와 엔진 연동은 `tests/ReviewGate.ProviderTests/CliReviewerTests.cs`에서 캡처한 출력 형태와 가짜 CLI로 검사하고, 실제 실행은 라이브 smoke로 확인합니다.
- 가격표는 2026-12-01 00:00 KST에 만료됩니다. 그 뒤에는 유료 모델 미리보기가 "가격표 유효기간이 지났습니다"로 막히므로 가격표를 갱신해야 합니다.
