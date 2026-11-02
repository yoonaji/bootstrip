# 부트다이어트(가칭)

Windows 시작 앱과 국내 금융 보안 모듈을 자동으로 찾아 **사용 / 사용 안 함 / 제거 / 확인 필요**로 판정하는 프로그램입니다.
지금은 구현 계획의 0~1단계(읽기 전용 수집 + 규칙 판정)까지 들어 있고, PC의 설정은 아무것도 바꾸지 않습니다.

## 구조

| 폴더 | 내용 | 테스트 위치 |
|---|---|---|
| `src/BootDiet.Core` | 데이터 모델, 명령줄 해석, 규칙 DB, 판정기 | 맥 |
| `src/BootDiet.Windows` | 레지스트리 Run 키·시작 폴더·StartupApproved 수집, 파일 버전·서명 읽기 | Windows |
| `src/BootDiet.Cli` | 명령줄 프로그램 `bootdiet` | 둘 다 |
| `tests/BootDiet.Tests` | xUnit 테스트 | 맥 |
| `rules/rules.json` | 판정 규칙 DB (위에 있는 규칙이 우선) | |
| `samples/` | 테스트용 시작 항목 목록 (스크린샷, 데스크톱 실측) | |

## 맥에서 개발하기

```bash
brew install --cask dotnet-sdk        # .NET 10 SDK
dotnet test                           # 테스트 실행
dotnet run --project src/BootDiet.Cli -- --input samples/screenshot-items.json
```

## Windows에서 실행하기

맥에서 Windows용 실행 파일을 만들 수 있습니다.

```bash
dotnet publish src/BootDiet.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

`publish/win-x64` 폴더를 통째로 Windows로 옮긴 뒤 터미널에서 실행합니다.

```powershell
.\bootdiet.exe                         # 이 PC의 시작 항목 판정
.\bootdiet.exe --save my-pc.json       # 수집한 목록 저장 (맥으로 가져가 --input 으로 테스트)
.\bootdiet.exe --json result.json      # 판정 결과 저장
```

서명하지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다. "추가 정보 > 실행"을 누르면 됩니다.

## 규칙 추가하기

`rules/rules.json`에 항목을 추가합니다. `match` 안의 조건은 하나라도 맞으면 적용됩니다.

```jsonc
{ "id": "kr-example", "product": "예시 보안 모듈", "vendor": "예시사",
  "category": "FinancialSecurity", "action": "Remove", "reason": "사용자에게 보여 줄 이유",
  "match": { "fileNames": ["example*.exe"], "pathContains": ["\\Example\\"], "companyContains": ["Example Co"] } }
```

`pathContains`에 사용자 폴더처럼 흔한 이름(`\ASUS\` 등)을 넣으면 `C:\Users\ASUS\...` 아래의 다른 앱까지 잘못 걸리니 주의하세요.

## 다음 단계

- 2단계 마무리: 규칙 보강, 서명 유효성 검증(WinVerifyTrust)
- 3단계: 성능·보안 수치 읽기 (StartupInfo XML, 부팅 이벤트 100/101, 메모리, 열린 포트)
- 4단계: 백업 → 끄기 → 되돌리기
