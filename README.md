# ReviewGate

ReviewGate는 코드, 설계 문서, 원고를 검토하고 결과를 원문 근거와 연결하는 Windows용 .NET CLI와 데스크톱 앱입니다. 여러 검토자의 결과를 비교하고 Markdown 보고서와 구조화된 결과를 만들 수 있습니다.

## 처음 사용하기

빌드 후 저장소 루트에서 아래 명령으로 데스크톱 앱을 엽니다.

```powershell
dotnet run --project src/ReviewGate.Desktop
```

1. **간이 테스트**에서 사용할 검수자와 검토 유형을 고르고, 검토 지시와 대상 텍스트 또는 파일을 지정합니다.
2. **미리보기**에서 입력과 검수자를 확인한 뒤 실행을 승인합니다. API 검수자를 쓰는 경우 **API 키** 탭에서 해당 제공자를 먼저 설정합니다.
3. 결과에서 지적 사항과 원문 근거를 확인합니다. 이전 결과는 **실행 기록**에서 다시 열 수 있습니다.

CLI를 먼저 살펴보려면 아래 빌드 절차의 `--help`를 실행하세요. 외부 모델 호출 없이 실행 흐름을 확인하는 방법은 [오프라인 사용 안내](docs/OFFLINE_USAGE.md)에 있습니다.

## 빌드

Windows와 `global.json`에 지정된 .NET 10 SDK가 필요합니다.

```powershell
dotnet restore ReviewGate.sln --locked-mode
dotnet build ReviewGate.sln -c Release --no-restore
dotnet run --project src/ReviewGate.Cli -- --help
```

명령과 종료 상태는 [CLI 계약](docs/CLI_AND_RUN_CONTRACT.md), 데스크톱 설정은 [데스크톱 안내](docs/DESKTOP.md)를 참고하세요. API 검토는 지정한 입력을 선택한 모델 제공자에게 전송하며 비용이 발생할 수 있습니다.
