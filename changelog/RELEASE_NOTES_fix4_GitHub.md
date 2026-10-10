**이 모드 원작은 따로 있습니다**

**원본 모드 : https://arca.live/b/tarkovspt/179689279**
**제작 : 파인애플애호가**

**대상: SPT 4.1.5 · 클라이언트 `RaidArmorRepair` 1.9.0 · 서버 `MiniArmorRepairKit` 1.2.0 (fix3과 동일)**

------------------------------------------------------------------------------------------------------------------------------------------------------

**<설치>**

- `RaidArmorRepair-1.9.0.zip` 안의 `RaidArmorRepair.dll` → `SPT\BepInEx\plugins\` 에 덮어쓰기
- 서버 모드(야전 수리 키트)는 **fix3에서 바뀐 게 없습니다.** 이미 설치하셨으면 그대로 두시면 됩니다.
  처음이면 `MiniArmorRepairKit-1.2.0.zip` 안의 파일을 `SPT\SPT_Runtime\user\mods\MiniArmorRepairKit\` 에 넣어 주세요(없어도 바닐라 수리 키트로 동작).
- 설정 파일은 지우지 않아도 됩니다. 새 설정은 기본값으로 추가됩니다.

**<변경점> (fix3 = 1.3.0 이후)**

1. 수리 중 봇이 총을 못 쏘던 버그 수정

수리 중 "내 총 발사 막기" 기능이 **맵 전체 봇의 사격까지** 막고 있던 문제를 고침. 이제 내 캐릭터만 막힘.

2. 무기 수리 키트가 방어구 수리에 쓰이던 버그 수정

게임 본래 판정(이 키트로 이 아이템을 고칠 수 있는지)을 통과한 **방어구 수리 키트만** 사용함.

3. 거의 다 찬 방어구를 조금만 고쳐도 최대내구도가 크게 깎이던 버그 수정

99/100 방어구를 1만 고쳐도 한 번 분량 전체만큼 최대내구도가 깎일 수 있었음. 이제 실제로 회복한 양에 비례해서 깎임.

4. 수리 작업 모션 추가 (기본)

J를 누르고 있는 동안 발전기 수리·퀘스트 아이템 설치 때와 같은 작업 자세를 취함. 멈춰 선 상태(서기·앉기)에서만 시작되고, 작업 중에는 이동 불가.
F12에서 `멀티툴로 작업`(기본, 자세 그대로) / `쪼그려 앉아 작업` 중 선택 가능.

5. 레거시 방식(모션 없음) 선택 가능

F12 `레거시 방식 사용 (모션 없음)`을 켜면 예전처럼 선 채로 수리. 이때 수리 중 이동 규칙을 `느리게 걷기`(기본, 최대 걷기 속도의 40%·달리기 불가) / `움직이면 취소` / `제한 없음` 중에서 고를 수 있음.

6. 진행 상황 한 줄 알림

화면 오른쪽 알림을 `전체 수리 완료까지: 63%` 한 줄로 표시 (입고 있는 방어구 전 부위의 현재 내구도 합 ÷ 최대 내구도 합).
다 고치면 `방어구 수리 완료`, 중간에 키를 떼면 `방어구 수리 중단 · 전체 수리 완료까지: 85%`.
부위별로 자세히 보고 싶으면 F12 `상세 수리 진행도 표시`를 켜면 됨(기본 꺼짐).

7. 수리 완료 효과음

입고 있는 방어구가 전부 고쳐지면, 메인 화면에서 상인에게 수리받을 때 나는 '수리 완료' 소리가 한 번 남. F12에서 끌 수 있음.

8. F12 설정 화면 한글화

모든 설정 이름·설명을 한글로 바꾸고 `1. 기본 / 2. 수리 동작 · 이동 / 3. 최대내구도 마모 / 4. 디버그`로 정리함. 설정 파일 안의 이름은 그대로라 기존 값은 유지됨.

9. 디버그 로그 스위치

F12 `4. 디버그 → 디버그 로그`(기본 꺼짐). 문제가 있을 때만 켜고 재현한 뒤 `BepInEx\LogOutput.log`를 보내 주시면 됨. 꺼져 있으면 평소엔 로그를 거의 남기지 않음.

10. 기타

방어구 재질을 4.1 정식 필드로 직접 읽도록 변경, 수리킷 툴팁의 남은 자원이 수리 직후 바로 갱신됨.

**<테스트>**

1.9.0 기본 동작(작업 모션·수리·한 줄 알림·완료 효과음) 인게임 확인. 레거시 방식의 이동 규칙 3종은 따로 확인하지 못했습니다.

------------------------------------------------------------------------------------------------------------------------------------------------------

**Changes since fix3 (English summary)**

- Fixed: while repairing, the trigger block also stopped **every bot on the map** from firing. Only your own character is blocked now.
- Fixed: weapon repair kits were consumed for armor. Only kits that pass the game's own `CanRepair` check are used.
- Fixed: a tiny repair on nearly-full armor could cost a full tick's worth of max durability. Wear now scales with the amount actually repaired.
- New: work animation while holding the repair key (same as generator repair / quest item placing). Starts only while standing still. A legacy no-animation mode with movement rules (slow walk / cancel on move / no limit) is available in F12.
- New: one-line progress notification (`전체 수리 완료까지: 63%` = overall durability of all worn armor). Detailed per-part messages are an F12 toggle.
- New: the trader "repair complete" sound plays once when all worn armor is fully repaired.
- New: Korean F12 settings with plain descriptions; a debug log switch.
- The server mod is unchanged from fix3.

------------------------------------------------------------------------------------------------------------------------------------------------------

**!!!!!!!!!!!PLEASE LEAD THIS!!!!!!!!!!!**

Hi everyone,

I'd like to share a few thoughts and important warnings regarding the process of porting mods from version 4.0 to 4.1.

Following the recent "ECOT" mod incident, I've had a lot of time to reflect on my actions.
I’m not here to lecture anyone—I’m writing this simply in the hope that you’ll keep these points in mind.


**1. I am not a professional programmer; I am mostly a "vibe coder" who relies heavily on AI.**

By that, I mean I’m not someone who spends days designing and painstakingly crafting mods by hand from scratch.
At the same time, having tinkered with simple mod edits back in the TS/JS (SPT 2.3.1) days, I know firsthand how much time, effort, And care goes into this process.
Therefore, even if a mod is covered under an MIT License, if the original author requests that I delete the source code and stop distribution, I will willingly comply.


**2. Please DO NOT re-distribute or re-upload these modified mods to 3rd/4th party places.**

Even if my ported version has some fixes or improvements, please do not treat it as a long-term solution. Unlike official releases on "THE FORGE," I do not perform thorough debugging or full security scans.
Furthermore, imagine how disruptive it would be if someone mistook my ported build for an "official release" and pestered the original author about bugs in my version. It would be terrible.
Moving forward, I intend to approach any porting work with the utmost respect for original modders.
If I had spent days pouring my heart into making a mod, only to have someone take my code and modify it however they pleased, I wouldn't feel good about it either.
Therefore, when writing README.md files on GitHub, I will always credit the original creators, And if it's not a mod I built from scratch, I will fully respect the original modder's decisions and opinions.


**3. Please respect the original creators.**

If people ignore warning 2. and act as if my port is the "real deal," I would feel immense guilt.
Please do not do that. As the saying goes, "When in Rome, do as the Romans do"—and showing proper respect and courtesy is essential.
Instead, please visit the original creators' comment sections on The Forge to leave warm words of appreciation, encouragement, or even buy them a coffee/leave a tip if you can :)


**Thank you for reading this long post.**
**Stay safe and stay happy!**

**^^7**


------------------------------------------------------------------------------------------------------------------------------------------------------


**!!!!!!!!!!!꼭 읽어주세요!!!!!!!!!!**

안녕하세요 여러분

4.0 -> 4.1로 포팅하는 과정에 대해 드릴 말씀과 주의사항을 전달하고자 합니다.

이번 "ECOT" 모드 사건 이후로, 많은 반성의 계기를 가지게 되었습니다

뭔가, 설교를 드리는게 아니라 참고 해주셨으면 하는 마음에 글을 작성합니다.


**1. 저는 전문 프로그래머가 아니고, AI를 주로 이용하는 바이브코더입니다.**

해당말의 뜻은, 저는 매일 구상하고 힘들게 한땀한땀 손으로 모드를 만드는 사람이 아니고

또한, TS/JS (SPT 2.3.1) 시절에 간단하게 모드 수정을 거치며 이 과정이 얼마나 노력을 들이고, 시간이 필요한지 잘 알고 있습니다.

그렇기에, 해당 모드를 수정하면서 아무리 MIT 라이선스를 받았더라도 원 모드 제작자가 소스코드 삭제 및 배포를 중단해달라고 하면 중단할 의향이있습니다.


**2. 무단으로 수정한 모드를 "3차 4차 배포하지 마십쇼"**

제가 만든 버전이 개선점이 있어도, 오래 사용하지 마세요 저는 "FORGE" 에 올린 것 처럼 바이러스 검사나 디버깅을 완벽하게 수행하지 않습니다.

또한, 제가 포팅한 버전을 "진짜 릴리즈된 버전" 이라고 생각하고, 제 포팅 모드를 들고 원작자에게 가면 얼마나 민폐일까요? 끔찍합니다

저는 모더들을 최대한 존중하는 방향으로 포팅할 계획입니다.

아마, 제가 모드를 제작할 때 하루를 갈아넣어 만들었는데 누군가가 멋대로 코드를 뺏겨 지 멋대로 작성했다면 기분을 썩 좋지않을 것 같습니다.

고로 GITHUB에 README.MD 작성 시, 원작자 표기 및 제가 실제로 제작하지 않은 모드라면 최대한 해당 모드를 제작한 모더의 의견을 존중하는 방향으로 나아갈 것입니다.


**3. 원작자를 존중해주세요**

만일 2번 주의사항을 어겼을 시, 제가 포팅한 모드가 "진짜 모드다!" 이런 느낌이면 진짜 죄책감에 휩싸일 것 같습니다.

그런 짓은 하지말아주고, 로마에 왔으면 로마법을 지키듯이 한국에서는 예의를 갖추는 것이 미덕입니다.

해당 모드 제작자의 FORGE COMMENT 창에 들어가 따뜻한 위로와 감사하다는 격려 또는 그들에게 TIP을 건네주세요 🙂


**긴 글 읽어주셔서 감사합니다.** 

**행복하세요! ^^7**
