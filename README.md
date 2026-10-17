# Area Fullscreen

Chrome 창을 모니터의 원하는 영역(예: 32:9 모니터의 절반)에 맞춰 두고 단축키를 누르면,
그 창이 있던 자리에 **브라우저 창 없이 동영상만** 꽉 차게 재생합니다. 모든 동영상 사이트에서 동작합니다.

## 구성

| 폴더 | 역할 |
| --- | --- |
| `extension/` | Chrome 확장앱. 가장 큰 동영상을 찾아 탭 화면에 꽉 채우고, 탭을 별도 팝업 창으로 옮깁니다. |
| `host/` | Windows 도우미 프로그램. 원래 창 자리에 테두리 없는 액자 창을 만들고, 팝업 창의 웹 화면 부분만 보이도록 그 안에 넣습니다. 액자 창을 옮기고 크기를 바꾸는 것도 도우미가 처리합니다. |

Chrome 은 탭바·주소창·테두리를 스스로 그리고, PIP 와 전체화면은 크기를 제한하기 때문에 확장앱만으로는 할 수 없어서 도우미 프로그램을 함께 씁니다.

## 설치

1. 도우미 프로그램을 빌드하고 Chrome 에 등록합니다. (관리자 권한 필요 없음, .NET Framework 4 는 Windows 에 기본 포함)

   ```bash
   powershell -ExecutionPolicy Bypass -File host\install.ps1
   ```

2. Chrome 에서 `chrome://extensions` 를 열고 **개발자 모드**를 켠 뒤 **압축해제된 확장 프로그램 로드**로 `extension` 폴더를 선택합니다.
   확장앱 ID 가 `jeblbgpopdkdlglillcdelanpgbhfhdi` 인지 확인합니다. (도우미가 이 ID 의 확장앱만 허용합니다)

## 사용법

1. Chrome 창을 원하는 영역에 배치하고 동영상을 엽니다.
2. `Alt+P` (또는 확장앱 아이콘 클릭) → 그 영역에 동영상만 꽉 찹니다.
3. 끄기: `Alt+P` 다시 누르기, `Esc`, 또는 `Alt+F4` → 탭이 원래 창으로 돌아갑니다.

- **옮기기**: 동영상 위를 끌면 창이 따라 움직입니다. (끌지 않고 클릭만 하면 평소처럼 동영상에 클릭이 전달되고, 영상 창이 맨 앞으로 옵니다)
  아래쪽 재생바 영역에서는 창이 옮겨지지 않아 재생 위치를 그대로 조절할 수 있습니다.
- **Windows 스냅**: 화면 가장자리·모서리로 끌면 절반/4분의 1 크기로 맞춰지고, 위쪽으로 끌면 스냅 레이아웃이 나타납니다.
- **PowerToys FancyZones**: 끄는 동안 Shift 를 누르면 영역이 표시되고, 놓으면 그 영역에 맞춰 들어갑니다.
- **처음 크기**: 영상 창은 모니터 면적의 25% 크기(동영상 비율 유지)로, 원래 Chrome 창의 가운데에 열립니다.
  바꾸려면 `extension/worker.js` 의 `INITIAL_AREA` 를 고칩니다. (`1` 이면 원래 Chrome 창 크기 그대로)
- **크기 조절**: 창 가장자리의 얇은 검은 테두리나 모서리를 끕니다.
- 창 이동은 Windows 기본 창 이동을 그대로 쓰므로 PowerToys FancyZones(Shift+드래그)와 Windows 스냅도 동작합니다.
- 영상 창을 클릭하면 맨 앞으로 나옵니다.
- 도우미 프로그램에 예상하지 못한 오류가 나면 `%TEMP%\AreaFullscreenHost.log` 에 기록됩니다.
- 재생/일시정지·탐색은 동영상 위의 기본 컨트롤이나 사이트 단축키(스페이스, 방향키 등)로 합니다.
- 영상 창으로 보는 동안에는 사이트의 전체화면 버튼(f 키, 더블클릭 포함)이 동작하지 않습니다. 이미 창이 영상만 보여 주고 있기 때문입니다.
- 단축키는 `chrome://extensions/shortcuts` 에서 바꿀 수 있습니다.
- 영상 창을 항상 다른 창 위에 띄우려면 `extension/worker.js` 의 `TOPMOST` 를 `true` 로 바꾸고 확장앱을 새로고침합니다.
- 아이콘에 `!` 가 뜨면 아이콘에 마우스를 올려 오류 내용을 확인하세요. 대부분 도우미 프로그램이 설치되지 않은 경우입니다.

## 제거

```bash
powershell -ExecutionPolicy Bypass -File host\uninstall.ps1
```

그 다음 `chrome://extensions` 에서 확장앱을 삭제합니다.

## 참고

- Chrome 전용입니다. (도우미 등록 위치가 `HKCU\Software\Google\Chrome\NativeMessagingHosts`)
- 모니터 배율(125% 등) 때문에 영상 영역이 1px 정도 어긋날 수 있습니다.
- 닫힌(closed) shadow DOM 안에 있는 동영상은 찾지 못합니다.
