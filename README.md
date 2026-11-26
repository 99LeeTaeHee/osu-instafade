# osu! Instafade — 서클 잔상 제거기  by LTH

osu!(stable) 스킨에서 **서클을 친 뒤 퍼지면서 남는 잔상**을 버튼 하나로 없애 주는 Windows 프로그램입니다.

## 다운로드
**[Releases](../../releases/latest)** 에서 `osu-Instafade-by-LTH.exe` 를 받으세요.
- 설치 필요 없음 (실행 환경이 exe 안에 모두 들어 있음)
- Windows 10 / 11 (64비트)
- 서명이 없는 프로그램이라 처음 실행할 때 "Windows의 PC 보호" 창이 뜨면 **[추가 정보] → [실행]**

## 사용법
1. exe 실행
2. `.osk` 파일이나 스킨 폴더를 창에 끌어다 놓기 (버튼으로 고르기, osu! 스킨 목록에서 고르기도 가능 · 여러 개 한꺼번에 가능)
3. **[잔상 제거하기]** 클릭
4. 원본 옆에 `스킨이름 (instafade).osk` 가 만들어짐 → **[osu!로 바로 가져오기]**

원본 스킨은 바뀌지 않습니다.

## 원리
osu!는 서클을 치면 서클 그림(`hitcircle`, `hitcircleoverlay`)은 퍼지며 천천히 사라지지만, 서클 위 숫자 그림(`default-1~9`)은 바로 사라집니다.
그래서 서클을 숫자 그림 안에 합쳐 넣고, 원래 서클 그림은 투명하게 만듭니다.

- osu!는 숫자를 서클의 0.8배로 그리므로 합칠 때 서클을 1.25배로 키움
- `HitCircleOverlayAboveNumber` 설정에 맞춰 겹치는 순서 결정
- 고화질(@2x)/일반 화질, 움직이는 테두리, 슬라이더 시작 원, 하위 폴더 숫자, 그림이 없는 스킨 등 대응
- 왼쪽 아래 콤보 카운터 숫자가 깨진 스킨이면 함께 고침

## 알아두기
- 콤보 10 이상에서는 숫자 두 개가 같은 자리에 겹쳐 보입니다 → 고급 설정에서 "서클 위 숫자 표시"를 끄면 깔끔합니다
- 콤보 색은 한 가지만 쓸 수 있습니다 (색을 그림에 미리 입히기 때문)
- osu!lazer는 설정의 "Hit animations"를 끄면 되므로 이 프로그램은 osu!(stable)용입니다

## 직접 빌드하기
.NET 8 SDK 필요
```
dotnet test tests/OsuInstafade.Tests
dotnet publish src/OsuInstafade.App -c Release -o publish_tmp
```

## 폴더 구성
- `src/OsuInstafade.Core` — 처리 엔진 (스킨 읽기, 그림 합성, .osk 저장)
- `src/OsuInstafade.App` — 화면 (WPF + [WPF-UI](https://github.com/lepoco/wpfui))
- `tests/OsuInstafade.Tests` — 자동 테스트
- `instafade.ps1` — 초기 버전 PowerShell 스크립트
- `구현계획.md` — 구현 계획서

포함된 오픈 소스 라이선스는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고.

---
made by LTH
