### ⚠️ IMPORTANT NOTICE / DISCLAIMER

**Original Author:** RaidArmorRepair author
**Original Repository:** RaidArmorRepair
**Original Link:** https://drive.google.com/drive/folders/1_ZyEmQPeDzEZioCpLmk_8d7zNYCJ2dTh?usp=sharing
**License:** No LICENSE file; mod metadata declares MIT
**This Port By:** R_F (danyhappy564-cmyk) — unofficial, AI-assisted port. Not affiliated with or endorsed by the original author.

1. **Reflection & Take-Downs:** I deeply reflect on the ECOT incident. As an AI-assisted "vibe coder," I will immediately delete files if the original authors ask.
2. **No Re-Distribution:** These ported builds are unverified, temporary fixes. Please do NOT re-upload or share them anywhere else.
3. **Do Not Pester Original Authors:** Never report bugs or pester original modders regarding issues from my unofficial ports.
4. **Full Credit & Respect:** I will always credit original creators on GitHub and prioritize their decisions above all else.
5. **Support Original Creators:** Instead of using my ports, please visit the original authors' Forge pages to leave kind words or tips.

---

# armorrepair-zzap4.1

레이드 중에 방어구를 수리하는 모드. **SPT 4.1.5** 용으로 포팅했습니다.

> **원작: RaidArmorRepair 제작자 ** — 원본 배포 폴더:
> https://drive.google.com/drive/folders/1_ZyEmQPeDzEZioCpLmk_8d7zNYCJ2dTh?usp=sharing
>
> 이 저장소는 **원작의 SPT 4.1 포팅**입니다. 원작 자체가 아닙니다.
> 원본에 LICENSE 파일이 없고 메타데이터에 `License = "MIT"` 로만 적혀 있어서,
> 재배포 전에 원작자에게 한 번 알려두시는 걸 권합니다.

---

## 뭐 하는 모드냐

레이드 중에 **J 키를 누르고 있으면** 착용 중인 방어구가 수리됩니다.

- 5초에 한 번씩 수리 (설정 가능)
- 누르고 있는 동안 계속, 키를 떼거나 **움직이면 취소**
- 수리하는 동안은 **총을 쏠 수 없음** (방아쇠 차단)
- 가방/조끼/주머니/보안 컨테이너 안의 **수리 키트를 자동으로 찾아** 자원을 소모
- 방탄판(플레이트)도 같이 수리
- 지력(Intellect) 스킬이 높을수록 한 번에 더 많이 회복되고, 최대내구도도 덜 깎임

## 알아두면 좋은 것 — 창이 뜨다 바로 꺼지면서 수리가 안 되는 문제

레이드에서 J를 눌렀을 때 "수리 시작" 알림만 뜨고, 진행률 창은 뜨자마자 곧 닫히면서
실제로는 내구도가 회복되지 않는 증상이 보고됐습니다.

**4.1.5 API 자체는 문제없습니다.** 이 모드가 쓰는 타입(`EquipmentSlot`,
`ArmorComponent.Repairable`, `RepairableComponent.Durability/.MaxDurability`,
`RepairKitComponent.Resource`, `ArmorHolderComponent.ArmorPlates`, `Item.RaiseRefreshEvent`
등)을 실제 4.1.5 클라이언트 어셈블리에 전부 대조했고, 이름·시그니처 전부 4.0과
동일합니다.

**실제 원인: 수리킷 없이 눌렀을 때의 메시지 순서.** 이 리포트는 수리킷을 인벤토리에
안 넣고 들어간 상태에서 키를 누른 경우로 확인됐습니다. 그 자체는 원래부터 정상
처리되고 있었습니다 — `TryRepairArmor`가 수리킷을 못 찾으면 "인벤토리에 사용 가능한
방어구 수리 키트가 없습니다." 를 명확히 알려주고 멈춥니다.

문제는 **순서**였습니다. 키를 누르면 가능 여부를 확인하기도 전에 무조건
"방어구 수리를 시작합니다..." 를 먼저 띄웠고, 그 다음 `RepairTickInterval`(기본
5초)이 다 지나야 첫 틱이 돌면서 그제서야 "키트가 없습니다"를 알려주고 창을 닫았습니다.
플레이어 입장에서는 "시작한다더니 몇 초 후 조용히 꺼지고 아무 일도 안 일어남"으로
보였을 겁니다 — 실패 메시지가 늦게, 그리고 성공을 암시하는 메시지 뒤에 나왔을 뿐,
로직 자체는 처음부터 맞았습니다.

**고친 것:** 키를 누르는 즉시(`StartRepairSession`에서) 방어구/수리킷 존재 여부를
먼저 확인합니다. 안 되는 상황이면 "시작합니다" 메시지 없이 **그 자리에서 바로** 정확한
실패 이유 한 줄만 뜨고 끝납니다. 되는 상황일 때만 기존처럼 "시작합니다" +
진행률 창이 뜹니다.

추가로, 혹시 몰라서 넣어둔 안전장치도 남겨뒀습니다: `TryRepairArmor`의 예외를
try/catch로 잡아 로그에 남기고(사용자에게도 "오류로 취소됨" 알림), UI 갱신
(`RaiseRefreshEvent`) 실패가 이미 적용된 내구도 회복을 무르지 않게 처리합니다.

> 진행률 창 표시 시간의 +2초 여유는 이후 **1.3.0에서 제거됐습니다** — 그 여유가
> 지키려던 상황을 위 try/catch가 이미 대신 처리하고 있어서 여유 자체가 불필요해졌고,
> 남겨두는 바람에 카운트다운이 매번 2초씩 더 길게 표시되는 부작용만 있었습니다.
> 자세한 내용은 아래 "1.3.0" 절 참고.

## 1.3.0 — 표시 시간 2초 오차 / 반응성 개선 (2026-09-16)

**신고: "실제로 4초 남았다고 뜨면 오버해서 6초라고 뜸"** — 정확히 맞았습니다.
`ShowProgressPanel`이 진행률 창에 `RepairTickInterval + 2초`를 넘기고 있었는데,
그 2초가 카운트다운 표시 자체의 시작값이라 **매 틱마다 항상 정확히 2초 더 길게
표시**되고 있었습니다.

그 +2초는 원래 안전장치였습니다 — 틱 처리 중 예외가 나서 우리 쪽 갱신이 씹히면
바닐라 진행률 창이 자기 타이머로 먼저 닫혀버리는 걸 막으려던 것(README 위쪽
"알아두면 좋은 것" 문단 참고). 근데 지금 코드에서는 `TryRepairArmor`가 그 예외를
이미 자체적으로 잡아서 `false`를 돌려주고, 그러면 `Update()`가 곧바로
`ResetRepairState()`→`ClosePanel()`을 불러서 창을 직접 닫습니다. 그 안전장치가
막으려던 상황 자체가 이제는 안 일어나서, +2초는 표시만 틀리게 만들고 있었을 뿐이라
제거했습니다.

**+ "누르는 즉시 수리"**: 키를 누르면 첫 틱까지 기다리지 않고 **그 즉시** 한 번
수리됩니다. 그 다음부터는 누르고 있는 동안 평소처럼 `RepairTickInterval`마다
반복됩니다. `RepairTickInterval` 기본값도 5초 → **1초**로 낮췄습니다(설정 가능
범위는 그대로 1~30초).

- 즉시 반응 + 1초 간격이라, `RepairPercentPerUse` 기본값(30%) 기준으로 완전
  수리까지 대략 3~4초면 끝납니다. 너무 빠르다 싶으면 F12에서
  `RepairPercentPerUse`나 `KitResourceCostPerUse`를 조절하세요 — 속도 자체를
  낮추고 싶으면 `RepairTickInterval`을 다시 올리면 됩니다.

## 구성

| 프로젝트 | 위치 | 하는 일 |
| --- | --- | --- |
| `RaidArmorRepair` | `BepInEx\plugins\` | 클라이언트 플러그인 — 수리 로직 전부 |
| `MiniArmorRepairKit` | `SPT\user\mods\MiniArmorRepairKit\` | 서버 모드 — 특수 슬롯에 들어가는 **야전 수리 키트** 아이템 추가 |

서버 모드는 없어도 됩니다. 없으면 바닐라 수리 키트만 쓰면 됩니다.

## 빌드

```
dotnet build RaidArmorRepair/RaidArmorRepair.csproj  -c Release
dotnet build MiniArmorRepairKit/MiniArmorRepairKit.csproj -c Release
```

SPT 설치 경로 기본값은 `E:\SPT 4.1` 입니다. 다르면:

```
dotnet build ... -c Release -p:SptRoot="D:\내SPT경로"
```

빌드하면 각각 위 표의 경로로 자동 복사됩니다.

## F12 설정 (`Raid Armor Repair`)

**General**

| 항목 | 기본값 | 설명 |
| --- | --- | --- |
| `RepairKey` | `J` | 수리 단축키 (누르고 있는 동안 수리) |
| `RepairPercentPerUse` | `0.3` | 1틱당 회복량 (최대내구도 대비 비율) |
| `KitResourceCostPerUse` | `20` | 1틱당 소모되는 수리킷 자원 |
| `RepairTickInterval` | `1` (1.3.0부터, 이전 `5`) | 몇 초마다 한 번 수리할지 — 첫 틱은 키를 누르는 즉시 적용 |
| `IntellectMaxBonusMultiplier` | `0.5` | 지력 최대일 때 회복량 추가 배율 (+50%) |
| `ShowNotifications` | `true` | 수리 시작/종료 알림 표시 |

**Degradation** (최대내구도 마모)

| 항목 | 기본값 | 설명 |
| --- | --- | --- |
| `EnableMaxDurabilityDegradation` | `true` | 수리할 때 최대내구도도 같이 깎을지 |
| `MaxDurabilityLossAtWorstCase` | `40` | 최악 조건(지력 0 · 저효율 재질)에서의 감소율 % |
| `MaxDurabilityLossAtBestCase` | `5` | 지력 최대일 때 남는 최소 감소율 % |
| `WorstCaseReferenceEfficiency` | `0.26` | 위 최악 감소율의 기준이 되는 수리 효율값 (세라믹) |
| `DefaultArmorMaterialEfficiency` | `1` | 재질을 못 읽었을 때 쓸 대체 효율값 |

> 원본 아카챈 설명에는 "현재는 최대내구도가 안 깎인다"고 되어 있었지만,
> 디컴파일된 코드에는 마모 계산이 들어 있었습니다. 그래서 기능은 살리되
> **`EnableMaxDurabilityDegradation = false` 로 끌 수 있게** 해뒀습니다.

---

## 4.1 포팅에서 바뀐 것

SPT 4.1은 클라이언트 어셈블리의 난독화를 풀었기 때문에, 타입 이름이 실제 이름으로 바뀌었습니다.
실제 4.1.5 `Assembly-CSharp.dll` 로 하나하나 확인한 결과 **바뀐 타입은 딱 3개**였습니다.

| 4.0 (난독화) | 4.1 (실제 이름) |
| --- | --- |
| `SkillClass` | `EFT.Skill` |
| `ArmorPlateItemClass` | `EFT.InventoryLogic.ArmorPlate` |
| `NotificationManagerClass` | `EFT.Communications.NotificationManager` |

`RepairableComponent`, `RepairKitComponent`, `InventoryEquipment`, `SkillManager`,
`ArmorComponent`, `EquipmentSlot`, `Player.FirearmController.SetTriggerPressed` 은
전부 그대로였습니다.

그 외:

- 클라이언트: `net4.8` → `netstandard2.1`, `ItemComponent.Types` / `Sirenix.Serialization` 참조 추가
- 서버: `net9.0` → `net10.0`, 패키지 `SPTarkov.*` → `SPTushonka.*` (4.1.5)
- 서버: `AbstractModMetadata` → `IModMetadata`, `IOnLoad.OnLoad()` → `OnLoadAsync(CancellationToken)`
- `GamePlayerOwner` 를 못 찾아도 죽지 않도록 3단계로 찾고, 실패하면 진행률 패널만 끄고 수리는 계속 동작

## ⚠️ 복원한 부분 (원본과 다를 수 있음)

받은 파일이 **디컴파일 결과물**이라, 일부 데이터가 소실된 상태였습니다.
아래 항목들은 제가 **아카챈 설명과 상식선에서 재구성한 값**이며, 원본과 미묘하게 다를 수 있습니다.

- **야전 수리 키트(Field Repair Kit)의 세부 설정 전부** — 서버 모드의 `OnLoad` 본문이
  디컴파일에서 통째로 날아가고 **아이템 ID 2개만** 남아 있었습니다. 그래서 아래는 전부 제 재구성입니다:
  - 아이템 이름 / 설명 / 한글 로케일
  - 가격 42,000₽, 핸드북 분류 (물물교환 → 도구)
  - 1칸 · 무게 5kg (아카챈 설명 기준)
  - 특수 슬롯 필터 등록 방식
  - **원본 `MiniArmorRepairKit.dll` 이 있으면 정확한 값으로 맞출 수 있습니다.**
- **두 개의 배열 값** — `RuntimeHelpers.InitializeArray` 로 컴파일돼 있어서 디컴파일에서
  내용이 사라졌습니다. 코드 사용처를 보고 재구성했습니다:
  - 수리 대상 슬롯: 방탄복 / 전술조끼 / 헤드기어
  - 수리킷 탐색 슬롯: 가방 / 전술조끼 / 주머니 / 보안 컨테이너
- **방어구 재질별 수리 효율표** — 바닐라 EFT 수치를 기준으로 채웠습니다
  (아라미드 1 · UHMWPE 3 · 강철 3 · 티타늄 0.63 · 복합 0.4 · 세라믹 0.26 · 유리 0.15)

## 실기동에서 잡은 버그

첫 실기동 로그에 이렇게 찍혔습니다:

```
[MiniArmorRepairKit] Field Repair Kit registered (...), allowed in 0 special slot(s)
```

아이템은 만들어졌는데 **특수 슬롯에 하나도 안 들어간** 겁니다. 이 모드의 핵심이 특수 슬롯인데요.

원인: 특수 슬롯은 기본 인벤토리 템플릿(`55d7217a4bdc2d86028b456d`)이 아니라 **주머니(Pockets)**
템플릿에 붙어 있고, 게다가 **템플릿이 2개**입니다 — 일반판과 Unheard 에디션판:

```
627a4e6b255f7527fb05a0f6
65e080be269cbd5c5005e529
```

WTT-ServerCommonLib 의 `SpecialSlotsHelper` 가 정확히 이 두 개를 쓰는 걸 보고 확인했습니다.
지금은 두 템플릿 모두에서 `SpecialSlot*` 슬롯을 찾아 필터를 넓히고, 필터가 아예 없는 슬롯에는
필터를 만들어 넣습니다. 몇 개 슬롯을 실제로 건드렸는지 로그에 찍히니 다음 기동 때 확인하실 수 있습니다.

## 상태

- 빌드: **성공** (두 프로젝트 모두, 1.3.0 이전 기준)
- 서버 기동: **확인됨** — 아이템 등록까지 정상
- 인게임 테스트: **아직 안 함** — 베타로 취급하세요

> ⚠️ **1.3.0의 세 변경(즉시 첫 틱 / +2초 여유 제거 / 기본 간격 1초)은 이 환경에
> .NET SDK가 없어 컴파일 검증을 못 했습니다.** 중괄호/괄호 균형만 확인했습니다.
> 빌드는 로컬에서 해주세요.
