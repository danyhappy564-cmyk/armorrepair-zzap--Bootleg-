using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace RaidArmorRepair
{
    /// <summary>What happens when the player moves while holding the repair key.</summary>
    public enum MoveWhileRepairingMode
    {
        /// <summary>Walk speed is capped and sprint is blocked for as long as the repair runs.</summary>
        SlowDown,
        /// <summary>Any movement ends the repair.</summary>
        Cancel,
        /// <summary>Movement is not touched (pre-1.5.0 behaviour).</summary>
        Allow,
    }

    [BepInPlugin("com.pineapplelover.raidarmorrepair", "레이드 방어구 수리 (Raid Armor Repair)", "1.7.0")]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>The objectives panel needs a GamePlayerOwner. It is not always up the
        /// moment the raid starts, so it is retried a few times before giving up — repairs
        /// still work without it, only the panel is lost.</summary>
        private const float OwnerRetryInterval = 1f;
        private const int OwnerRetryMax = 15;

        public static ManualLogSource Log;

        public static ConfigEntry<KeyboardShortcut> RepairKey;
        public static ConfigEntry<float> RepairPercentPerUse;
        public static ConfigEntry<float> KitResourceCostPerUse;
        public static ConfigEntry<float> RepairTickInterval;
        public static ConfigEntry<float> IntellectMaxBonusMultiplier;
        public static ConfigEntry<bool> ShowNotifications;
        public static ConfigEntry<bool> ShowTickDetails;
        public static ConfigEntry<bool> PlayRepairSound;
        public static ConfigEntry<bool> EnableMaxDurabilityDegradation;
        public static ConfigEntry<float> MaxDurabilityLossAtWorstCase;
        public static ConfigEntry<float> MaxDurabilityLossAtBestCase;
        public static ConfigEntry<float> WorstCaseReferenceEfficiency;
        public static ConfigEntry<float> DefaultArmorMaterialEfficiency;
        public static ConfigEntry<string> MoveWhileRepairing;

        // Stored in the .cfg as-is, so these strings are the saved values — do not reword them
        // without a migration.
        private const string MoveSlowDown = "느리게 걷기";
        private const string MoveCancel = "움직이면 취소";
        private const string MoveAllow = "제한 없음";

        private static MoveWhileRepairingMode MoveMode =>
            MoveWhileRepairing.Value == MoveCancel ? MoveWhileRepairingMode.Cancel
            : MoveWhileRepairing.Value == MoveAllow ? MoveWhileRepairingMode.Allow
            : MoveWhileRepairingMode.SlowDown;
        public static ConfigEntry<float> RepairMoveSpeedMultiplier;
        public static ConfigEntry<bool> UseLegacyRepair;
        public static ConfigEntry<string> RepairMotion;

        private const string MotionMultitool = "멀티툴로 작업";
        private const string MotionCrouch = "쪼그려 앉아 작업";

        /// <summary>
        /// How long one Plant state lasts before the game ends it on its own. The repair key is
        /// expected to be released long before this; if it is not, MaintainMotion re-enters.
        /// </summary>
        private const float MotionPlantTime = 600f;

        /// <summary>The PlantAction we installed, so we only ever end our own Plant state and
        /// never a vanilla one (generator repair, quest item placing).</summary>
        private Action<bool> _motionCallback;
        private Player _motionPlayer;

        /// <summary>
        /// Key for our entry in MovementContext's speed-limit table. The game keys that table by
        /// Player.ESpeedLimit, and Add() refuses a cause that is already present, so borrowing one
        /// of the game's own causes (Armor, Shot, ...) would either be ignored or wipe the game's
        /// limit on Remove(). An out-of-range value is safe: the table uses EnumHelper's
        /// comparer, which compares the underlying integers, and the limit is only ever folded in
        /// as "lowest value wins".
        /// </summary>
        private const Player.ESpeedLimit RepairSpeedLimitCause = (Player.ESpeedLimit)0x5241;

        /// <summary>States that count as "moving" for MoveWhileRepairingMode.Cancel.</summary>
        private static readonly EPlayerState[] MovingStates =
        {
            EPlayerState.Run,
            EPlayerState.Sprint,
            EPlayerState.ProneMove,
            EPlayerState.Jump,
            EPlayerState.Roll,
            EPlayerState.ClimbOver,
            EPlayerState.ClimbUp,
        };

        private Player _slowedPlayer;
        private float _appliedSpeedLimit;

        private float _repairTimer;
        private Player _lastKnownPlayer;
        private GamePlayerOwner _owner;
        private bool _panelShown;
        private float _ownerRetryTimer;
        private int _ownerRetryCount;

        private void Awake()
        {
            Log = Logger;

            // The .cfg section/key names stay English so values saved by earlier versions are
            // kept; only what F12 shows (category, name, description, order) is Korean.
            // ConfigurationManager lists higher Order first within a category.
            const string basic = "1. 기본";
            const string movement = "2. 수리 동작 · 이동";
            const string wear = "3. 최대내구도 마모";

            RepairKey = Bind(basic, 100, "General", "RepairKey", new KeyboardShortcut(KeyCode.J),
                "수리 단축키",
                "누르고 있는 동안 지금 입고 있는 방어구(방탄복·조끼·헬멧과 그 안의 판)를 수리합니다. 손을 떼면 멈춥니다.");

            RepairPercentPerUse = Bind(basic, 90, "General", "RepairPercentPerUse", 0.3f,
                "한 번에 고치는 양",
                "한 번 고칠 때 그 부위 최대내구도의 몇 %를 채울지입니다. 지력 보너스는 여기에 더해집니다.\n예) 30% → 최대내구도 60인 판은 한 번에 18 회복",
                new AcceptableValueRange<float>(0.05f, 1f), percent: true);

            RepairTickInterval = Bind(basic, 80, "General", "RepairTickInterval", 1f,
                "수리 간격 (초)",
                "키를 누르면 바로 한 번 고치고, 그 뒤로 몇 초마다 다시 고칠지입니다. 숫자가 클수록 느리게 고쳐집니다.",
                new AcceptableValueRange<float>(1f, 30f));

            KitResourceCostPerUse = Bind(basic, 70, "General", "KitResourceCostPerUse", 20f,
                "한 번에 쓰는 수리킷 양",
                "한 번을 꽉 채워 고칠 때 수리킷 자원을 얼마나 쓸지입니다. 조금만 고치면 그만큼 덜 씁니다.",
                new AcceptableValueRange<float>(1f, 200f));

            IntellectMaxBonusMultiplier = Bind(basic, 60, "General", "IntellectMaxBonusMultiplier", 0.5f,
                "지력 최대일 때 추가 회복량",
                "지력 스킬이 최대(51)일 때 한 번에 고치는 양이 얼마나 늘어나는지입니다. 지력이 낮으면 비례해서 줄어듭니다.\n예) 50% → 지력 최대면 1.5배, 지력 25쯤이면 약 1.25배",
                new AcceptableValueRange<float>(0f, 2f), percent: true);

            ShowNotifications = Bind(basic, 50, "General", "ShowNotifications", true,
                "알림 표시",
                "수리 시작·종료, 수리킷 없음 같은 화면 오른쪽 위 알림을 보여줄지입니다. 끄면 '수리할 때마다 상세 알림'도 같이 꺼집니다.");

            ShowTickDetails = Bind(basic, 40, "General", "ShowTickDetails", true,
                "수리할 때마다 상세 알림",
                "고칠 때마다 어느 방어구의 어느 부위가 얼마나 고쳐졌는지 알려줍니다.\n예) 수리: 6B13 앞판(SAPI) 31→49/60 (+18) · 최대 64→60 · 키트 -12 (남음 188)");

            PlayRepairSound = Bind(basic, 30, "General", "PlayRepairSound", true,
                "수리 효과음",
                "고칠 때마다 메인 화면에서 상인에게 수리받을 때 나는 '수리 완료' 소리를 냅니다.");

            RepairMotion = Bind(movement, 120, "Movement", "RepairMotion", MotionMultitool,
                "작업 모션 종류",
                "수리하는 동안 캐릭터가 할 동작입니다. (발전기 수리·퀘스트 아이템 설치 때와 같은 동작)\n멀티툴로 작업 : 자세는 그대로 두고 손으로 작업하는 동작\n쪼그려 앉아 작업 : 자세를 낮추고 작업하는 동작\n작업 중에는 걸을 수 없고 시야가 좁게 고정됩니다. 키를 떼면 바로 일어납니다.",
                new AcceptableValueList<string>(MotionMultitool, MotionCrouch));

            UseLegacyRepair = Bind(movement, 110, "Movement", "UseLegacyRepair", false,
                "레거시 방식 사용 (모션 없음)",
                "켜면 작업 모션 없이 예전처럼 선 채로 수리합니다. 이때는 아래 '레거시:' 두 설정이 적용됩니다.\n끄면(기본) 작업 모션으로 수리하고, 멈춰 선 상태(서기·앉기)에서만 시작할 수 있습니다.");

            MoveWhileRepairing = Bind(movement, 100, "Movement", "MoveWhileRepairing", MoveSlowDown,
                "레거시: 수리 중에 움직이면",
                "느리게 걷기 : 천천히 걷기만 되고 달리기는 안 됩니다.\n움직이면 취소 : 걷기·달리기·점프를 하면 수리가 멈춥니다.\n제한 없음 : 평소처럼 자유롭게 움직입니다.",
                new AcceptableValueList<string>(MoveSlowDown, MoveCancel, MoveAllow));

            RepairMoveSpeedMultiplier = Bind(movement, 90, "Movement", "RepairMoveSpeedMultiplier", 0.4f,
                "레거시: 느리게 걷기 속도",
                "'느리게 걷기'일 때 평소 최대 걷기 속도의 몇 %까지만 움직일 수 있는지입니다.",
                new AcceptableValueRange<float>(0.1f, 1f), percent: true);

            EnableMaxDurabilityDegradation = Bind(wear, 100, "Degradation", "EnableMaxDurabilityDegradation", true,
                "수리하면 최대내구도도 줄어듦",
                "상인 수리처럼 고칠 때마다 최대내구도가 조금씩 줄어듭니다. 끄면 몇 번을 고쳐도 최대내구도가 그대로입니다.");

            MaxDurabilityLossAtWorstCase = Bind(wear, 90, "Degradation", "MaxDurabilityLossAtWorstCase", 40f,
                "최대 감소량 (지력 0, 세라믹 기준, %)",
                "지력 0으로 세라믹 판을 한 번 꽉 채워 고칠 때 최대내구도가 몇 % 줄어들지입니다.\n튼튼한 재질(강철·UHMWPE)은 훨씬 덜 줄고, 유리는 더 많이 줄어듭니다. 조금만 고치면 그만큼 덜 줄어듭니다.",
                new AcceptableValueRange<float>(0f, 100f));

            MaxDurabilityLossAtBestCase = Bind(wear, 80, "Degradation", "MaxDurabilityLossAtBestCase", 5f,
                "최소 감소량 (지력 최대, %)",
                "지력이 최대(51)일 때 줄어드는 양입니다. 재질과 상관없이 한 번 꽉 채워 고치면 최소 이만큼은 줄어듭니다.",
                new AcceptableValueRange<float>(0f, 100f));

            WorstCaseReferenceEfficiency = Bind(wear, 70, "Degradation", "WorstCaseReferenceEfficiency", 0.26f,
                "[고급] 기준 재질 값",
                "'최대 감소량'이 어떤 재질 기준인지 정하는 값입니다. 0.26 = 세라믹. 보통은 건드릴 필요가 없습니다.\n(재질별 값: 유리 0.15 · 세라믹 0.26 · 복합 0.4 · 티타늄/알루미늄 0.63 · 아라미드 1 · 강철/UHMWPE 3)",
                new AcceptableValueRange<float>(0.01f, 10f));

            DefaultArmorMaterialEfficiency = Bind(wear, 60, "Degradation", "DefaultArmorMaterialEfficiency", 1f,
                "[고급] 재질을 모를 때 쓸 값",
                "방어구 재질을 알아낼 수 없을 때 대신 쓸 값입니다. 1 = 아라미드(연질 방탄)와 같게 취급. 보통은 건드릴 필요가 없습니다.",
                new AcceptableValueRange<float>(0.01f, 10f));

            Log.LogInfo("Raid Armor Repair loaded.");

            new Harmony("com.pineapplelover.raidarmorrepair").PatchAll();
        }

        private ConfigEntry<T> Bind<T>(string category, int order, string section, string key, T defaultValue,
            string displayName, string description, AcceptableValueBase acceptable = null, bool percent = false)
        {
            var attributes = new ConfigurationManagerAttributes
            {
                Category = category,
                Order = order,
                DispName = displayName,
                ShowRangeAsPercent = percent ? true : (bool?)null,
            };

            return Config.Bind(section, key, defaultValue, new ConfigDescription(description, acceptable, attributes));
        }

        private void Update()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                ResetRepairState();
                return;
            }

            Player mainPlayer = Singleton<GameWorld>.Instance.MainPlayer;
            if (mainPlayer == null || mainPlayer.HealthController == null || !mainPlayer.HealthController.IsAlive)
            {
                ResetRepairState();
                return;
            }

            TrackOwner(mainPlayer);

            KeyboardShortcut key = RepairKey.Value;
            if (key.IsDown())
            {
                StartRepairSession(mainPlayer);
            }
            else if (RepairService.IsRepairing && !key.IsPressed())
            {
                // Released, or any other reason the key stopped being held.
                ResetRepairState();
            }

            if (!RepairService.IsRepairing)
            {
                return;
            }

            if (!ApplyMovementRules(mainPlayer))
            {
                return;
            }

            _repairTimer += Time.deltaTime;
            if (_repairTimer < RepairTickInterval.Value)
            {
                return;
            }

            // Subtract rather than zero, so a long frame does not silently eat progress.
            _repairTimer -= RepairTickInterval.Value;

            if (RepairService.TryRepairArmor(mainPlayer))
            {
                ShowProgressPanel(mainPlayer);
            }
            else
            {
                ResetRepairState();
            }
        }

        /// <summary>
        /// Runs every frame while a repair is in progress. Returns false when it ended the
        /// session (Cancel mode and the player moved).
        /// </summary>
        private bool ApplyMovementRules(Player player)
        {
            if (!UseLegacyRepair.Value)
            {
                ReleaseSpeedLimit();
                return MaintainMotion(player);
            }

            ExitMotion();

            switch (MoveMode)
            {
                case MoveWhileRepairingMode.Cancel:
                    ReleaseSpeedLimit();
                    if (IsMoving(player))
                    {
                        RepairService.Notify(player, "움직여서 방어구 수리를 멈췄습니다.");
                        ResetRepairState();
                        return false;
                    }

                    return true;

                case MoveWhileRepairingMode.SlowDown:
                    ApplySpeedLimit(player);
                    return true;

                default:
                    ReleaseSpeedLimit();
                    return true;
            }
        }

        // ---- work motion ---------------------------------------------------------

        /// <summary>
        /// The Plant state can only be entered from IdlePlayerState (standing or crouched, not
        /// moving, not prone) — every other state's Plant() is an empty override, so calling it
        /// anyway would silently do nothing. The vanilla generator repair checks the same thing.
        /// </summary>
        private static bool CanStartMotion(Player player)
        {
            try
            {
                return player.MovementContext?.CurrentState is IdlePlayerState;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 작업 모션 가능 여부 확인 실패: " + ex.Message);
                return false;
            }
        }

        private static bool IsInPlantState(Player player)
        {
            MovementContext movement = player.MovementContext;
            return movement != null && movement.PlantState != null && movement.CurrentState == movement.PlantState;
        }

        /// <summary>
        /// The same call the generator repair (ClientRunddansController.Repair) and the transit
        /// confirmation make. multitool=true keeps the pose and plays the multitool animation;
        /// false lowers the pose first and restores it on exit (PlantPlayerState.Enter/Exit).
        /// </summary>
        private void EnterMotion(Player player)
        {
            try
            {
                if (!CanStartMotion(player))
                {
                    return;
                }

                bool multitool = RepairMotion.Value != MotionCrouch;
                Action<bool> callback = _ => { };
                player.CurrentManagedState.Plant(true, multitool, MotionPlantTime, callback);

                if (IsInPlantState(player))
                {
                    _motionCallback = callback;
                    _motionPlayer = player;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 작업 모션 시작 실패 (모션 없이 수리는 계속됨): " + ex.Message);
            }
        }

        /// <summary>
        /// Keeps the motion going while the key is held. The Plant state blocks walking and
        /// jumping by itself (its Move/Jump do nothing), so the only ways out are its own timer
        /// — re-entered here — or something else forcing a state change, which ends the repair
        /// rather than leaving the player repairing on their feet.
        /// </summary>
        private bool MaintainMotion(Player player)
        {
            try
            {
                if (_motionPlayer == player && IsInPlantState(player))
                {
                    return true;
                }

                if (CanStartMotion(player))
                {
                    EnterMotion(player);
                    return true;
                }

                if (_motionPlayer == null)
                {
                    // The motion never started (an exception in EnterMotion); keep repairing
                    // without it instead of refusing outright.
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 작업 모션 유지 확인 실패: " + ex.Message);
                return true;
            }

            RepairService.Notify(player, "작업 자세가 풀려서 방어구 수리를 멈췄습니다.");
            ResetRepairState();
            return false;
        }

        /// <summary>Stands the player back up, but only out of the Plant state we started.</summary>
        private void ExitMotion()
        {
            if (ReferenceEquals(_motionPlayer, null))
            {
                return;
            }

            try
            {
                // Leave a dead player's states to the death handling.
                bool alive = _motionPlayer != null && _motionPlayer.HealthController != null
                             && _motionPlayer.HealthController.IsAlive;
                MovementContext movement = alive ? _motionPlayer.MovementContext : null;
                if (movement != null && movement.CurrentState == movement.PlantState
                    && ReferenceEquals(movement.PlantAction, _motionCallback))
                {
                    movement.PlantAction = null;
                    movement.ExitOverridenState();
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 작업 모션 종료 실패: " + ex.Message);
            }

            _motionPlayer = null;
            _motionCallback = null;
        }

        private static bool IsMoving(Player player)
        {
            try
            {
                if (player.IsSprintEnabled)
                {
                    return true;
                }

                EPlayerState state = player.CurrentState?.Name ?? EPlayerState.None;
                return Array.IndexOf(MovingStates, state) >= 0;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 이동 상태 확인 실패, 정지로 처리: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Caps walk speed the same way the game does for overweight (an absolute cap, scaled
        /// by MovementContext.MaxSpeed), and drops sprint: the speed-limit table only caps the
        /// walk speed, sprint has its own limit the table does not touch.
        /// </summary>
        private void ApplySpeedLimit(Player player)
        {
            try
            {
                MovementContext movement = player.MovementContext;
                if (movement == null)
                {
                    return;
                }

                float limit = RepairMoveSpeedMultiplier.Value * movement.MaxSpeed;
                if (_slowedPlayer != player || Math.Abs(_appliedSpeedLimit - limit) > 0.001f)
                {
                    ReleaseSpeedLimit();
                    movement.AddStateSpeedLimit(limit, RepairSpeedLimitCause);
                    _slowedPlayer = player;
                    _appliedSpeedLimit = limit;
                }

                if (player.IsSprintEnabled)
                {
                    player.EnableSprint(false);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 수리 중 이동 속도 제한 실패 (수리는 계속됨): " + ex.Message);
            }
        }

        private void ReleaseSpeedLimit()
        {
            // Reference check, not Unity's ==: a destroyed player still has to be forgotten here.
            if (ReferenceEquals(_slowedPlayer, null))
            {
                return;
            }

            try
            {
                // Unity's null check: the player object may already be destroyed (raid ended).
                if (_slowedPlayer != null && _slowedPlayer.MovementContext != null)
                {
                    _slowedPlayer.MovementContext.RemoveStateSpeedLimit(RepairSpeedLimitCause);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 이동 속도 제한 해제 실패: " + ex.Message);
            }

            _slowedPlayer = null;
            _appliedSpeedLimit = 0f;
        }

        private void TrackOwner(Player mainPlayer)
        {
            if (_lastKnownPlayer != mainPlayer)
            {
                _lastKnownPlayer = mainPlayer;
                _owner = ResolveOwner(mainPlayer);
                _ownerRetryTimer = 0f;
                _ownerRetryCount = 0;
                return;
            }

            if (_owner != null || _ownerRetryCount >= OwnerRetryMax)
            {
                return;
            }

            _ownerRetryTimer += Time.deltaTime;
            if (_ownerRetryTimer < OwnerRetryInterval)
            {
                return;
            }

            _ownerRetryTimer = 0f;
            _ownerRetryCount++;
            _owner = ResolveOwner(mainPlayer);

            if (_owner == null && _ownerRetryCount >= OwnerRetryMax)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner를 여러 번 재시도해도 못 찾음. 이번 레이드에서는 진행률 패널을 비활성화합니다 (수리 자체는 정상 동작).");
            }
        }

        /// <summary>
        /// Checks up front rather than letting the first tick discover it 5 seconds later. Field
        /// report: a player with no repair kit in their inventory pressed the key, saw "repair
        /// starting", and then — a full RepairTickInterval later — saw the panel close with no
        /// heal applied. That is not a bug: TryRepairArmor already declines cleanly and notifies
        /// "인벤토리에 사용 가능한 방어구 수리 키트가 없습니다." But leading with "시작합니다"
        /// when the attempt is already doomed (no kit, or armor already at full durability) reads
        /// as broken even though every step behind it is correct. Checking here means a hopeless
        /// press gets exactly one message, immediately, instead of a misleading "started" followed
        /// by a delayed failure that looks like a silent close.
        /// </summary>
        private void StartRepairSession(Player player)
        {
            RepairPreview preview = RepairService.Peek(player);
            if (!preview.HasTarget)
            {
                RepairService.Notify(player, "수리할 방어구가 없거나 이미 최대 내구도입니다.");
                return;
            }

            if (!preview.HasKit)
            {
                RepairService.Notify(player, "인벤토리에 사용 가능한 방어구 수리 키트가 없습니다.");
                return;
            }

            if (!UseLegacyRepair.Value && !CanStartMotion(player))
            {
                RepairService.Notify(player, "작업 모션은 멈춰 선 상태(서기·앉기)에서만 시작할 수 있습니다. 움직이거나 엎드린 상태에서는 멈춘 뒤 다시 눌러주세요.");
                return;
            }

            if (UseLegacyRepair.Value && MoveMode == MoveWhileRepairingMode.Cancel && IsMoving(player))
            {
                RepairService.Notify(player, "움직이는 중에는 수리할 수 없습니다. 멈춘 상태에서 다시 눌러주세요.");
                return;
            }

            RepairService.IsRepairing = true;
            _repairTimer = 0f;
            RepairService.NotifyRepairStart(player);

            if (!UseLegacyRepair.Value)
            {
                EnterMotion(player);
            }

            // Field report (2026-09): holding the key felt unresponsive - press it and
            // nothing visibly happens for a full RepairTickInterval. Apply the first tick
            // immediately instead of waiting for Update()'s timer to reach it, so the key
            // repairs right away and then keeps going on the normal cadence for as long as
            // it's held. TryRepairArmor is the exact same call the periodic tick makes -
            // it already updates durability/kit resource and is safe to call here too.
            if (RepairService.TryRepairArmor(player))
            {
                ShowProgressPanel(player);
            }
            else
            {
                // The immediate tick already finished the job (or the kit ran out on it) -
                // nothing left to wait for. NotifySessionEnd only fires if that tick actually
                // healed something, so a same-press "시작합니다" + "종료: +X" pair is expected
                // and correct, not a duplicate message.
                ResetRepairState();
            }
        }

        /// <summary>
        /// The vanilla panel (EFT.UI.BattleUIPanelExtraction) closes itself once the duration
        /// passed to Show() runs out - confirmed by decompiling the client, present in both 4.0
        /// and 4.1, not something this mod controls. We re-show it every RepairTickInterval, so
        /// passing exactly that value as the duration keeps the displayed countdown on the same
        /// clock as the real time to the next tick - the number shown is the number that's true.
        ///
        /// This used to carry a flat +2s margin (see the README's "복원한 부분" / bug-fix
        /// notes) so the panel would outlive a missed refresh - an exception mid-tick, or a
        /// dropped frame - instead of the vanilla auto-close firing with no explanation.
        /// Field report (2026-09): the margin worked, but it also meant the countdown read
        /// 2 seconds ahead of the real remaining time on every single tick, not just the rare
        /// one it was guarding against - press J with 4s actually left and the panel said 6s.
        /// That guard is redundant now anyway: TryRepairArmor (RepairService.cs) already
        /// catches everything a tick can throw and returns false instead of letting it escape,
        /// which routes straight back to ResetRepairState()/ClosePanel() here rather than
        /// leaving the vanilla auto-close to fire on its own. So the margin was paying for a
        /// race that can no longer happen, at the cost of a countdown that was wrong every
        /// time it was shown. Removed; if a future change reopens that race, fix it by closing
        /// the panel explicitly on the failure path (which ResetRepairState already does),
        /// not by re-inflating the displayed number.
        /// </summary>
        private void ShowProgressPanel(Player player)
        {
            if (_owner == null)
            {
                return;
            }

            try
            {
                _owner.ShowObjectivesPanel(
                    RepairService.BuildProgressLabel(RepairService.Peek(player)),
                    RepairTickInterval.Value);
                _panelShown = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 진행률 패널 표시 실패: " + ex.Message);
            }
        }

        private void ClosePanel()
        {
            if (_panelShown && _owner != null)
            {
                try
                {
                    _owner.CloseObjectivesPanel();
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[RaidArmorRepair] 진행률 패널 닫기 실패: " + ex.Message);
                }
            }

            _panelShown = false;
        }

        /// <summary>Three escalating lookups: the player object, its children, then the whole
        /// scene. Any of them can throw during a scene change, so each is guarded separately.</summary>
        private GamePlayerOwner ResolveOwner(Player player)
        {
            try
            {
                GamePlayerOwner onPlayer = player.gameObject.GetComponent<GamePlayerOwner>();
                if (onPlayer != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: player.gameObject에서 발견");
                    return onPlayer;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner GetComponent 실패: " + ex.Message);
            }

            try
            {
                GamePlayerOwner inChildren = player.gameObject.GetComponentInChildren<GamePlayerOwner>(true);
                if (inChildren != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: player 자식 오브젝트에서 발견");
                    return inChildren;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner GetComponentInChildren 실패: " + ex.Message);
            }

            try
            {
                GamePlayerOwner anywhere = FindObjectOfType<GamePlayerOwner>();
                if (anywhere != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: FindObjectOfType으로 발견");
                    return anywhere;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner FindObjectOfType 실패, 진행률 패널 비활성화: " + ex.Message);
            }

            return null;
        }

        private void ResetRepairState()
        {
            bool wasRepairing = RepairService.IsRepairing;

            RepairService.IsRepairing = false;
            _repairTimer = 0f;
            ClosePanel();
            ReleaseSpeedLimit();
            ExitMotion();

            if (wasRepairing && _lastKnownPlayer != null)
            {
                RepairService.NotifySessionEnd(_lastKnownPlayer);
            }
        }
    }
}
