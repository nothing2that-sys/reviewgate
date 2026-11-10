# ReviewGate

ReviewGate는 코드, 설계 문서, 원고를 검토하고 결과를 원문 근거와 연결하는 Windows용 .NET CLI와 데스크톱 앱입니다. 여러 검토자의 결과를 비교하고 Markdown 보고서와 구조화된 결과를 만들 수 있습니다.

## 빌드

Windows와 `global.json`에 지정된 .NET 10 SDK가 필요합니다.

```powershell
dotnet restore ReviewGate.sln --locked-mode
dotnet build ReviewGate.sln -c Release --no-restore
dotnet run --project src/ReviewGate.Cli -- --help
```

명령과 종료 상태는 [CLI 계약](docs/CLI_AND_RUN_CONTRACT.md), 데스크톱 설정은 [데스크톱 안내](docs/DESKTOP.md)를 참고하세요. API 검토는 지정한 입력을 선택한 모델 제공자에게 전송하며 비용이 발생할 수 있습니다.

