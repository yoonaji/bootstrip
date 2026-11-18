# 보안 정책 (Security Policy)

부트다이어트는 관리자 권한으로 시작 프로그램·서비스·예약 작업을 바꾸고 제거 프로그램을 실행합니다.
그래서 이 프로그램 자체의 보안을 가장 중요하게 다룹니다. 코드를 공개하는 이유도 같습니다.
관리자 권한을 달라는 프로그램이라면, 누구나 무엇을 하는지 직접 확인할 수 있어야 한다고 생각합니다.
안전은 코드를 숨기는 데서 오지 않고, 코드가 모두 보여도 안전하게 만드는 데서 와야 합니다(Kerckhoffs 원칙).

## 지원 버전

| 버전 | 보안 수정 |
| --- | --- |
| main 브랜치 최신 | 지원 |
| 그 밖의 배포본 | 최신으로 바꿔 주세요 |

## 취약점 제보

**공개 이슈로 올리지 마세요.** 고치기 전에 공개되면 사용자가 위험해집니다.

1. 이 레포의 **Security → Report a vulnerability**(GitHub 비공개 취약점 제보)로 보내 주세요.
2. 재현 방법, 영향(예: 일반 권한에서 관리자 권한 획득), 사용한 버전·커밋을 적어 주시면 빠르게 확인할 수 있습니다.

| 단계 | 목표 |
| --- | --- |
| 받았다는 답 | 3일 안 |
| 영향 판단과 계획 공유 | 7일 안 |
| 수정 배포 | 심각도 높음 30일 안 |
| 공개 | 수정 배포 뒤 GitHub Security Advisory로 공개, 제보자 이름을 원하시면 함께 적습니다 |

## 범위

부트다이어트가 막아야 하는 것(범위 안):

- PC 안에서 **일반 권한으로 도는 프로그램**이 부트다이어트를 이용해 관리자 권한을 얻는 것
  (부트다이어트가 관리자로 읽는 파일·레지스트리·DLL·예약 작업을 바꿔치기하는 등)
- 같은 PC의 다른 사용자가 부트다이어트 파일로 정보를 보거나 동작을 바꾸는 것
- 변조된 배포본·규칙 파일을 진짜처럼 보이게 하는 것
- 프로그램 이름·설명·웹 검색 결과로 AI 판정을 조작해 위험한 처리를 하게 만드는 것(프롬프트 인젝션)
- 부트다이어트가 백신·Windows 필수 서비스·회사 보안 프로그램을 끄게 만드는 것
- API 키나 스캔 결과(개인정보)가 새는 것

범위 밖:

- 이미 **관리자 권한을 가진** 악성 프로그램이 하는 일(관리자는 Windows에서도 보안 경계가 아닙니다)
- 부트다이어트가 정리하는 다른 회사 제품 자체의 취약점(해당 회사나 KISA에 제보해 주세요)
- 사회공학, 물리적 접근

## 자체 점검

- 위협 모델(STRIDE)과 자체 침투 테스트 계획·결과를 공개합니다. 고친 취약점은 GitHub Security Advisory로 남깁니다.
- 모든 PR에서 빌드·테스트, CodeQL 정적 분석, 의존성 취약점 검사, 비밀 정보 검사가 자동으로 돕니다.

## English summary

Please report vulnerabilities privately via **Security → Report a vulnerability** on this repository, not in public issues.
We aim to acknowledge within 3 days and fix high-severity issues within 30 days, then publish a GitHub Security Advisory.
In scope: local privilege escalation from a standard-user process through BootDiet, tampering with files BootDiet reads
while elevated, AI prompt injection leading to unsafe actions, and leaks of API keys or scan data.
Out of scope: malware that already runs as administrator, and vulnerabilities in third-party products BootDiet manages.
