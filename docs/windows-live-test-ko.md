# ZoomCheck Windows 실회의 테스트

공동호스트 Windows PC에서 Excel 명단과 진행 중인 Zoom 회의 참가자를 공식 API로 대조하는 절차입니다. Business 계정은 Server-to-Server OAuth를, Pro 경로는 회의 내 Zoom App SDK를 사용합니다.

## 준비

1. GitHub Release에서 `ZoomCheck-Setup-x64.exe`를 받아 설치하거나 portable ZIP을 풉니다.
2. Business 모드는 PowerShell에서 `ZOOMCHECK_Zoom__AccountId`, `ZOOMCHECK_Zoom__ClientId`, `ZOOMCHECK_Zoom__ClientSecret`을 설정하고 OAuth 앱에 `dashboard:read:list_meeting_participants:admin` scope를 추가합니다.
3. Pro 모드는 README.ko.md의 앱 관리자 최초 설정을 완료하고 `ZOOMCHECK_ZoomApp__HomeUrl`을 실제 HTTPS Home URL로 설정합니다. 컴패니언을 열 계정이 ZoomCheck 앱을 설치할 수 있어야 합니다.
4. `ZoomCheck.Backend.exe`를 실행합니다. 브라우저에서 `http://127.0.0.1:5078` 대시보드가 열립니다.
5. 옆 Windows 기기에서 대상 Zoom 회의가 실제로 진행 중인지 확인합니다. ZoomCheck와 Zoom Workplace가 같은 PC에서 실행될 필요는 없습니다.

## 실시간 대조

1. 호스트가 운영 계정을 공동호스트로 지정합니다. Pro 모드에서는 지정이 끝난 뒤 Zoom App을 여세요.
2. 실제 회의 ID를 입력합니다. 이전 회의 ID가 남아 있으면 지우거나 현재 ID로 교체합니다.
3. 번호·성명 열이 있는 `.xlsx` 또는 `.xls` 명단을 올리고 표시된 명단 인원을 Excel과 대조합니다.
4. 연결 모드를 `자동`으로 둡니다. Pro 모드는 매 회의 새 6자리 코드를 생성하고 **Apps → ZoomCheck**에서 입력합니다. 앱을 닫거나 새로고침하면 새 코드로 재연결합니다.
5. 상단 **지금 동기화**를 누르고 Zoom의 현재 인원과 대시보드의 현재 참석/미매칭 합계를 비교합니다.
6. 상단 **자동 동기화**를 켜고 설정 팝업에서 10초 간격으로 둡니다.
7. 테스트 참가자 한 명을 퇴장·재입장시켜 입장/퇴장 이벤트가 각각 한 번 추가되는지 확인합니다.
8. 참가자 행을 눌러 원래 Zoom 이름, 자동 정리 이름, 현재 연결, 입·퇴장 상세를 확인합니다.
9. 같은 명단 인물이 두 기기로 접속했을 때 중복 연결에 표시되지만 출석은 한 명으로 계산되는지 확인합니다.
10. 검토 필요 또는 미매칭 이름을 확인하고 마지막에 CSV를 내려받습니다. Pro 모드는 이메일을 읽지 않으므로 이름 기준 결과를 더 주의해서 검토합니다.

회의 종료 후 Business 모드는 첫 0명 응답 경고에서 **전원 퇴장 확정**을 누릅니다. Pro 모드는 첫 빈 스냅샷을 보류하고 20초 안에 들어온 다음 빈 스냅샷으로 자동 확정합니다.

## 합격 기준

- Excel 행 수와 ZoomCheck 명단 인원이 같다.
- Zoom의 현재 참가자가 ZoomCheck의 현재 참석 또는 미매칭 목록에 빠짐없이 나타난다.
- 실제 퇴장/재입장 1회가 각각 이벤트 1건으로 반영된다.
- 자동 동기화 중 API 오류나 빈 응답이 기존 참석자를 임의로 퇴장 처리하지 않는다.
- CSV 행 수가 Excel 명단 인원과 같고 상태가 대시보드와 일치한다.

## 문제 해결

- `401`: Account ID, Client ID, Client Secret과 OAuth 앱 활성화를 확인합니다.
- `403`: Dashboard API 계정 접근과 required scope를 확인합니다. 공동호스트 권한만으로 해결되지 않습니다.
- `404`: 회의 ID, 회의 진행 상태, 회의가 OAuth 앱 계정에 속하는지 확인합니다.
- `429`: 자동 동기화 간격을 늘리고 잠시 뒤 다시 시도합니다.
- Pro 앱에 `호스트 또는 공동호스트가 아닙니다`: 앱을 닫고, 호스트가 공동호스트로 지정한 뒤 다시 엽니다.
- Pro 앱 연결이 사라짐: 대시보드에서 새 페어링 코드를 만든 뒤 다시 연결합니다.
- 0명 응답 경고: 회의에 참가자가 있다면 기존 상태를 유지하고 재시도합니다. 실제 0명이면 1회 허용 옵션으로 확정합니다.

활동 시각은 API에서 변화를 관측한 시각이라 실제 입퇴장보다 한 폴링 간격 늦을 수 있습니다.
