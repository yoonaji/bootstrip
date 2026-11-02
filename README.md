# 부트다이어트(가칭)

Windows 시작 앱과 국내 금융 보안 모듈을 자동으로 찾아 **제품 단위로** 묶고 **유지 / 사용 안 함 / 뱅킹 모드 / 제거 / 확인 필요**로 판정하는 프로그램입니다.
지금은 구현 계획의 0~2단계 일부(읽기 전용 수집: 시작 앱·서비스·드라이버·예약 작업 + 규칙 판정 + 제품 묶기)까지 들어 있고, PC의 설정은 아무것도 바꾸지 않습니다.

요구사항과 설계의 기준은 [프로젝트 문서](https://claude.ai/artifact/YZaEuk5QrcCcvVSu5JVpon)의 "요구사항 명세"·"설계" 탭입니다.

## 용어

- **구성 요소(Component)**: 자동 실행 단위 하나. 시작 앱(Run 키·시작 폴더), 서비스, 예약 작업, 드라이버, 루트 인증서, 방화벽 규칙.
- **제품(Product)**: 같은 프로그램에 속한 구성 요소 묶음. 화면에 보여 주고 처리하는 단위.

## 구조

| 폴더 | 내용 | 테스트 위치 |
|---|---|---|
| `src/BootDiet.Core` | 데이터 모델(Component, Product, Verdict), 명령줄 해석, 규칙 DB, 판정·제품 묶기 | 어디서나 |
| `src/BootDiet.Windows` | Run 키·시작 폴더·StartupApproved, 서비스·드라이버(레지스트리), 예약 작업(작업 스케줄러 COM) 수집, 파일 버전·서명 읽기 | Windows |
| `src/BootDiet.Cli` | 명령줄 프로그램 `bootdiet` | 둘 다 |
| `tests/BootDiet.Tests` | xUnit 테스트 | 어디서나 |
| `rules/rules.json` | 판정 규칙 DB (위에 있는 규칙이 우선) | |
| `samples/` | 테스트용 구성 요소 목록 (스크린샷, 데스크톱 실측) | |

## 맥에서 개발하기

```bash
brew install --cask dotnet-sdk        # .NET 10 SDK
dotnet test                           # 테스트 실행
dotnet run --project src/BootDiet.Cli -- scan --input samples/screenshot-items.json
```

## Windows에서 실행하기

맥에서 Windows용 실행 파일을 만들 수 있습니다.

```bash
dotnet publish src/BootDiet.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

`publish/win-x64` 폴더를 통째로 Windows로 옮긴 뒤 터미널에서 실행합니다.

```powershell
.\bootdiet.exe scan                         # 이 PC를 스캔해 제품별로 판정 (scan은 생략 가능)
.\bootdiet.exe scan --save my-pc.json       # 수집한 구성 요소 저장 (다른 PC에서 --input 으로 테스트)
.\bootdiet.exe scan --json result.json      # 판정 결과(제품 목록) 저장
.\bootdiet.exe help                         # 사용법
```

서명하지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다. "추가 정보 > 실행"을 누르면 됩니다.

## 규칙 추가하기

`rules/rules.json`에 항목을 추가합니다. `match` 안의 조건은 하나라도 맞으면 적용되고, 파일에서 위에 있는 규칙이 먼저 적용됩니다.
같은 규칙에 걸린 구성 요소는 한 제품으로 묶입니다. "메신저"처럼 여러 제품에 걸친 분류용 규칙에는 `"perApp": true`를 넣어 앱마다 따로 묶이게 합니다.
서비스·드라이버·인증서용 조건은 `serviceNames`, `driverNames`, `certNames`입니다.

```jsonc
{ "id": "kr-example", "product": "예시 보안 모듈", "vendor": "예시사",
  "category": "FinancialSecurity", "action": "Remove", "reason": "사용자에게 보여 줄 이유",
  "match": { "fileNames": ["example*.exe"], "pathContains": ["\\Example\\"], "companyContains": ["Example Co"] } }
```

경로 비교 전에 사용자 폴더(`C:\Users\<이름>\`)는 `%USERPROFILE%\`로 바뀌므로, 사용자 이름이 `ASUS`여도 `\ASUS\` 조건에 걸리지 않습니다.

## 다음 단계

- 2단계 마무리: 스토어 앱 StartupTask, 서명 유효성 검증(WinVerifyTrust). 시작 앱·서비스·드라이버·예약 작업·루트 인증서·방화벽 수집기는 완료(Windows 자체 항목은 판정 전에 뺌)
- 3단계: 성능·보안 수치 읽기 (StartupInfo XML, 부팅 이벤트 100/101, 메모리, 열린 포트)
- 4단계: 백업 → 끄기 → 되돌리기
