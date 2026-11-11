using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace VRSim
{
    /// <summary>
    /// 公口 / 母口对接。
    ///
    /// 吸附后两个口当成一个刚体：
    ///   一只手握着任意一端 → 这一端是「基准」，跟手走；另一端是「从端」，
    ///   位置/旋转都由脚本按吸附时的相对关系锁死（关掉 trackPosition/trackRotation，
    ///   否则 XRI 的手部驱动会把从端拽走，这就是之前「吸附和捏住打架」的原因）。
    ///   两只手各握一个 → 先握的那端继续当基准，另一端仍然锁死；
    ///   松开基准那只手 → 自动把基准换成还握着的那端，不会掉。
    ///   从端那只手拉出 pulloutDistance → 脱开，两端恢复跟手。
    ///
    /// 松手：还连着 → 整体缓动回记录的合体位；没连 → 各自回原位。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlugPair : MonoBehaviour
    {
        [Header("Plugs")]
        [Tooltip("可选：绑定螺丝装配总管后，背板没打开之前插头不可抓")]
        [SerializeField]
        ScrewAssemblyManager manager;
        [SerializeField]
        Transform malePlug;
        [SerializeField]
        Transform femalePlug;
        [SerializeField]
        XRGrabInteractable maleGrab;
        [SerializeField]
        XRGrabInteractable femaleGrab;

        [Header("Dock Pose")]
        [Tooltip("合体时公口的世界坐标")]
        [SerializeField]
        Vector3 dockMalePosition;
        [Tooltip("合体时母口的世界坐标")]
        [SerializeField]
        Vector3 dockFemalePosition;
        [SerializeField]
        bool dockPoseSet;

        [Header("Snap")]
        [Tooltip("两个口中心距离小于该值即吸附（米）。默认 3.6cm，必须小于两个口初始间距")]
        public float snapDistance = 0.036f;
        [Tooltip("吸附后，从端那只手拉出多远才脱开（米）")]
        public float pulloutDistance = 0.06f;
        [Tooltip("松手后回到合体位 / 原位的时间（秒）")]
        public float moveDuration = 0.15f;
        [Tooltip("打印吸附状态变化")]
        public bool debugLog = false;

        [Header("Feedback")]
        [Tooltip("吸附时给两个口染色，方便确认当前是否已连接")]
        public bool useDockHighlight = true;
        public Color dockTint = new Color(0.62f, 1f, 0.72f, 1f);

        enum State { Idle, Snapped, Moving }

        State m_State = State.Idle;

        // 两端的握持状态（自己记，避免 selectExited 里 isSelected 还没刷新的时序问题）
        bool m_MaleHeld, m_FemaleHeld;
        IXRSelectInteractor m_MaleInteractor, m_FemaleInteractor;

        // 吸附时的基准端 / 从端在基准端本地坐标系里的固定关系
        Transform m_Master;
        Vector3 m_SlaveLocalPos;
        Quaternion m_SlaveLocalRot;

        Vector3 m_MaleHome, m_FemaleHome;
        Quaternion m_MaleHomeRot, m_FemaleHomeRot;

        // 合体位换算到两端本地坐标系（会跟着插头姿态一起转）
        Vector3 m_LocalOffsetFromMale;
        Vector3 m_LocalOffsetFromFemale;

        bool m_Init;

        Renderer[] m_MaleRenderers;
        Renderer[] m_FemaleRenderers;
        MaterialPropertyBlock m_Block;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        Vector3 m_FromMale, m_ToMale, m_FromFemale, m_ToFemale;
        Quaternion m_FromMaleRot, m_ToMaleRot, m_FromFemaleRot, m_ToFemaleRot;
        float m_MoveT;
        State m_EndState = State.Idle;

        public bool IsDocked { get { return m_State == State.Snapped; } }
        public bool IsSnapped { get { return m_State == State.Snapped; } }

        void Awake()
        {
            EnsureInit();
        }

        void EnsureInit()
        {
            if (m_Init) return;
            if (malePlug == null || femalePlug == null || maleGrab == null || femaleGrab == null) return;

            m_Init = true;

            m_MaleHome = malePlug.position;
            m_MaleHomeRot = malePlug.rotation;
            m_FemaleHome = femalePlug.position;
            m_FemaleHomeRot = femalePlug.rotation;

            if (!dockPoseSet)
            {
                dockMalePosition = m_MaleHome;
                dockFemalePosition = m_FemaleHome;
                dockPoseSet = true;
            }

            RebuildDockOffsets();

            maleGrab.selectEntered.AddListener(OnSelected);
            maleGrab.selectExited.AddListener(OnReleased);
            femaleGrab.selectEntered.AddListener(OnSelected);
            femaleGrab.selectExited.AddListener(OnReleased);

            m_MaleRenderers = malePlug.GetComponentsInChildren<Renderer>(true);
            m_FemaleRenderers = femalePlug.GetComponentsInChildren<Renderer>(true);
            m_Block = new MaterialPropertyBlock();

            SetKinematic(malePlug);
            SetKinematic(femalePlug);
        }

        void OnDestroy()
        {
            if (!m_Init) return;
            if (maleGrab != null)
            {
                maleGrab.selectEntered.RemoveListener(OnSelected);
                maleGrab.selectExited.RemoveListener(OnReleased);
            }
            if (femaleGrab != null)
            {
                femaleGrab.selectEntered.RemoveListener(OnSelected);
                femaleGrab.selectExited.RemoveListener(OnReleased);
            }
        }

        void RebuildDockOffsets()
        {
            Vector3 dockOffset = dockFemalePosition - dockMalePosition;
            m_LocalOffsetFromMale = Quaternion.Inverse(m_MaleHomeRot) * dockOffset;
            m_LocalOffsetFromFemale = Quaternion.Inverse(m_FemaleHomeRot) * (-dockOffset);
        }

        // ---------- 主循环 ----------

        void Update()
        {
            EnsureInit();
            if (!m_Init) return;

            UpdateGrabAvailability();

            switch (m_State)
            {
                case State.Moving:
                    UpdateMove();
                    break;

                case State.Snapped:
                    CheckPullout();
                    break;

                default:
                    EnsureKinematicWhenIdle();
                    CheckSnap();
                    break;
            }
        }

        /// <summary>
        /// 从端的位置必须在 XRI 手部驱动之后再写，否则会被覆盖回手的位置。
        /// </summary>
        void LateUpdate()
        {
            if (m_State != State.Snapped || m_Master == null) return;

            Transform slave = m_Master == malePlug ? femalePlug : malePlug;
            if (slave == null) return;

            Vector3 pos = m_Master.TransformPoint(m_SlaveLocalPos);
            Quaternion rot = m_Master.rotation * m_SlaveLocalRot;
            SetPose(slave, pos, rot);
        }

        // ---------- 抓取事件 ----------

        void OnSelected(SelectEnterEventArgs args)
        {
            var grab = args.interactableObject as XRGrabInteractable;
            if (grab == null) return;

            bool isMale = grab == maleGrab;
            if (isMale)
            {
                m_MaleHeld = true;
                m_MaleInteractor = args.interactorObject;
            }
            else
            {
                m_FemaleHeld = true;
                m_FemaleInteractor = args.interactorObject;
            }

            Transform grabbed = isMale ? malePlug : femalePlug;

            // 回位动画途中被抓：立即交回给手，别让脚本继续拽
            if (m_State == State.Moving)
            {
                m_State = State.Idle;
                m_Master = null;
                SetTrack(maleGrab, true);
                SetTrack(femaleGrab, true);
                ApplyDockHighlight(false);
            }

            if (m_State == State.Snapped)
            {
                // 基准还握在手里 → 新抓的这端只能当从端，禁止被手驱动
                if (m_Master != null && m_Master != grabbed && IsHeld(m_Master))
                    SetTrack(grab, false);
                else
                    SetMaster(grabbed, false);   // 否则由这端接管，关系保持不变
            }
            else
            {
                SetTrack(grab, true);
            }

            if (debugLog)
                Debug.Log("[PlugPair] grab " + grabbed.name + " state=" + m_State + " master=" + (m_Master != null ? m_Master.name : "-"));
        }

        void OnReleased(SelectExitEventArgs args)
        {
            var grab = args.interactableObject as XRGrabInteractable;
            if (grab == null) return;

            bool wasMale = grab == maleGrab;
            if (wasMale)
            {
                m_MaleHeld = false;
                m_MaleInteractor = null;
            }
            else
            {
                m_FemaleHeld = false;
                m_FemaleInteractor = null;
            }

            if (debugLog)
                Debug.Log("[PlugPair] release " + grab.name + " state=" + m_State + " maleHeld=" + m_MaleHeld + " femaleHeld=" + m_FemaleHeld);

            if (m_State == State.Moving) return;

            if (m_State == State.Snapped)
            {
                if (!m_MaleHeld && !m_FemaleHeld)
                {
                    // 两只手都松了：整体回到记录的合体位
                    MoveToDock();
                }
                else if (m_Master == null || !IsHeld(m_Master))
                {
                    // 基准那只手松了，交棒给还握着的一端（继续单手拿两个）
                    SetMaster(wasMale ? femalePlug : malePlug, false);
                }
                return;
            }

            if (!m_MaleHeld && !m_FemaleHeld)
                ReturnBoth();
        }

        bool IsHeld(Transform t)
        {
            if (t == malePlug) return m_MaleHeld;
            if (t == femalePlug) return m_FemaleHeld;
            return false;
        }

        // ---------- 吸附 ----------

        void CheckSnap()
        {
            if (malePlug == null || femalePlug == null) return;
            if (!m_MaleHeld && !m_FemaleHeld) return;

            if (Vector3.Distance(malePlug.position, femalePlug.position) > snapDistance) return;

            EnterSnap();
        }

        void EnterSnap()
        {
            m_State = State.Snapped;

            // 先握着的一端当基准，两只手都在时用公口
            Transform master = m_MaleHeld ? malePlug : femalePlug;
            SetMaster(master, true);
            ApplyDockHighlight(true);

            if (debugLog)
                Debug.Log("[PlugPair] SNAP master=" + master.name
                          + " gap=" + Vector3.Distance(malePlug.position, femalePlug.position).ToString("F4"));
        }

        /// <summary>
        /// 指定基准端。snapIntoPlace=true 时把从端摆到合体位相对关系上（进入吸附）；
        /// false 时保持当前姿态关系（换基准 / 从静止状态被抓起）。
        /// </summary>
        void SetMaster(Transform master, bool snapIntoPlace)
        {
            m_Master = master;
            Transform slave = master == malePlug ? femalePlug : malePlug;

            if (snapIntoPlace)
            {
                Vector3 localOffset = master == malePlug ? m_LocalOffsetFromMale : m_LocalOffsetFromFemale;
                SetPose(slave, master.position + master.rotation * localOffset, master.rotation);
            }

            m_SlaveLocalPos = master.InverseTransformPoint(slave.position);
            m_SlaveLocalRot = Quaternion.Inverse(master.rotation) * slave.rotation;

            // 基准跟手，从端交给脚本
            SetTrack(maleGrab, master == malePlug);
            SetTrack(femaleGrab, master == femalePlug);

            // 从端由脚本直接摆位，必须是 kinematic；
            // 基准端只在“没被手握着”时锁住 —— 握着的一端要留给 XRI 驱动
            SetKinematic(slave);
            if (!IsHeld(master))
                SetKinematic(master);
        }

        /// <summary>只有两只手都握着时才可能拔出：从端的手相对从端拉开一定距离。</summary>
        void CheckPullout()
        {
            if (!m_MaleHeld || !m_FemaleHeld) return;
            if (m_Master == null) return;

            Transform slave = m_Master == malePlug ? femalePlug : malePlug;
            XRGrabInteractable slaveGrab = slave == malePlug ? maleGrab : femaleGrab;
            if (slaveGrab == null || !slaveGrab.isSelected) return;

            float d = Vector3.Distance(GetHandPosition(slaveGrab), slave.position);
            if (d > pulloutDistance)
            {
                if (debugLog) Debug.Log("[PlugPair] pullout d=" + d.ToString("F4"));
                ExitSnap();
            }
        }

        void ExitSnap()
        {
            m_State = State.Idle;
            m_Master = null;
            SetTrack(maleGrab, true);
            SetTrack(femaleGrab, true);
            ApplyDockHighlight(false);

            if (debugLog) Debug.Log("[PlugPair] EXIT SNAP");
        }

        // ---------- 移动 ----------

        void MoveToDock()
        {
            m_Master = null;
            StartMove(dockMalePosition, m_MaleHomeRot, dockFemalePosition, m_FemaleHomeRot, State.Snapped);
            ApplyDockHighlight(true);
        }

        void ReturnBoth()
        {
            ApplyDockHighlight(false);

            bool maleHome = Vector3.Distance(malePlug.position, m_MaleHome) < 0.004f;
            bool femaleHome = Vector3.Distance(femalePlug.position, m_FemaleHome) < 0.004f;
            if (maleHome && femaleHome) return;

            StartMove(m_MaleHome, m_MaleHomeRot, m_FemaleHome, m_FemaleHomeRot, State.Idle);
        }

        void StartMove(Vector3 maleTarget, Quaternion maleRot, Vector3 femaleTarget, Quaternion femaleRot, State endState)
        {
            // 动画用脚本直接摆位，两端都必须是 kinematic
            SetKinematic(malePlug);
            SetKinematic(femalePlug);

            m_FromMale = malePlug.position;
            m_ToMale = maleTarget;
            m_FromMaleRot = malePlug.rotation;
            m_ToMaleRot = maleRot;

            m_FromFemale = femalePlug.position;
            m_ToFemale = femaleTarget;
            m_FromFemaleRot = femalePlug.rotation;
            m_ToFemaleRot = femaleRot;

            m_MoveT = 0f;
            m_EndState = endState;
            m_State = State.Moving;
        }

        void UpdateMove()
        {
            m_MoveT += Time.deltaTime / Mathf.Max(moveDuration, 0.01f);
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(m_MoveT));

            SetPose(malePlug, Vector3.Lerp(m_FromMale, m_ToMale, t), Quaternion.Slerp(m_FromMaleRot, m_ToMaleRot, t));
            SetPose(femalePlug, Vector3.Lerp(m_FromFemale, m_ToFemale, t), Quaternion.Slerp(m_FromFemaleRot, m_ToFemaleRot, t));

            if (m_MoveT < 1f) return;

            m_State = m_EndState;

            if (m_State == State.Snapped)
            {
                // 停在合体位，保持刚体关系，随时可以被单手整串拿走
                m_Master = malePlug;
                m_SlaveLocalPos = malePlug.InverseTransformPoint(femalePlug.position);
                m_SlaveLocalRot = Quaternion.Inverse(malePlug.rotation) * femalePlug.rotation;
                SetTrack(maleGrab, true);
                SetTrack(femaleGrab, false);
            }
            else
            {
                m_Master = null;
                SetTrack(maleGrab, true);
                SetTrack(femaleGrab, true);
            }
        }

        // ---------- 抓取开关 ----------

        void UpdateGrabAvailability()
        {
            if (manager == null) return;
            bool canGrab = manager.IsBackPlateRemoved;
            SetGrabEnabled(maleGrab, canGrab);
            SetGrabEnabled(femaleGrab, canGrab);
        }

        static void SetGrabEnabled(XRGrabInteractable grab, bool value)
        {
            if (grab == null) return;
            if (grab.isSelected) return;
            if (grab.enabled != value) grab.enabled = value;
        }

        // ---------- 工具 ----------

        static void SetTrack(XRGrabInteractable grab, bool value)
        {
            if (grab == null) return;
            grab.trackPosition = value;
            grab.trackRotation = value;
        }

        static Vector3 GetHandPosition(XRGrabInteractable grab)
        {
            if (grab == null) return Vector3.zero;
            var list = grab.interactorsSelecting;
            if (list == null || list.Count == 0) return grab.transform.position;

            var attach = list[0].GetAttachTransform(grab);
            return attach != null ? attach.position : grab.transform.position;
        }

        static void SetPose(Transform t, Vector3 pos, Quaternion rot)
        {
            if (t == null) return;
            t.SetPositionAndRotation(pos, rot);

            var rb = t.GetComponent<Rigidbody>();
            if (rb != null && rb.isKinematic)
            {
                rb.position = pos;
                rb.rotation = rot;
            }
        }

        static void SetKinematic(Transform t)
        {
            if (t == null) return;
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                rb.useGravity = false;
            }
        }

        /// <summary>
        /// 空闲时只锁“没被握住”的一端。
        /// 握着的一端必须留给 XRI：Velocity Tracking 模式会在抓取时把刚体切成非 kinematic
        /// 并用速度驱动，如果这里每帧强锁 kinematic，就会变成“抓得住但拿不走”。
        /// </summary>
        void EnsureKinematicWhenIdle()
        {
            if (maleGrab == null || !maleGrab.isSelected) SetKinematic(malePlug);
            if (femaleGrab == null || !femaleGrab.isSelected) SetKinematic(femalePlug);
        }

        // ---------- 吸附状态染色 ----------

        void ApplyDockHighlight(bool on)
        {
            if (!useDockHighlight) return;
            Tint(m_MaleRenderers, on);
            Tint(m_FemaleRenderers, on);
        }

        void Tint(Renderer[] renderers, bool on)
        {
            if (renderers == null || m_Block == null) return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;

                if (on)
                {
                    r.GetPropertyBlock(m_Block);
                    m_Block.SetColor(BaseColorId, dockTint);
                    m_Block.SetColor(ColorId, dockTint);
                    r.SetPropertyBlock(m_Block);
                }
                else
                {
                    r.SetPropertyBlock(null);
                }
            }
        }

        // ---------- 编辑器辅助 ----------

        /// <summary>把当前两个口的位置记成合体位（编辑器里右键菜单调用）。</summary>
        [ContextMenu("记录当前两端口位置为合体位")]
        void CaptureDockPose()
        {
            if (malePlug == null || femalePlug == null)
            {
                Debug.LogWarning("[PlugPair] 还没绑定公口/母口");
                return;
            }

            dockMalePosition = malePlug.position;
            dockFemalePosition = femalePlug.position;
            dockPoseSet = true;

            // 记录时两端就摆在合体姿态上，用它们的当前旋转重建本地偏移
            m_MaleHomeRot = malePlug.rotation;
            m_FemaleHomeRot = femalePlug.rotation;
            RebuildDockOffsets();

            Debug.Log("[PlugPair] 合体位已记录 公口=" + dockMalePosition.ToString("F4")
                      + " 母口=" + dockFemalePosition.ToString("F4")
                      + " 间距=" + Vector3.Distance(dockMalePosition, dockFemalePosition).ToString("F4"));
        }

        /// <summary>调试用：把状态强行复位成未吸附。</summary>
        [ContextMenu("重置为未吸附")]
        void ResetSnapped()
        {
            ExitSnap();
        }

        void OnDrawGizmosSelected()
        {
            if (malePlug == null || femalePlug == null) return;

            Gizmos.color = new Color(0.2f, 0.9f, 0.6f, 0.5f);
            Gizmos.DrawLine(malePlug.position, femalePlug.position);

            Gizmos.color = new Color(0.2f, 0.9f, 0.6f, 0.25f);
            Gizmos.DrawWireSphere((malePlug.position + femalePlug.position) * 0.5f, snapDistance);

            if (dockPoseSet)
            {
                Gizmos.color = new Color(1f, 0.7f, 0.15f, 0.9f);
                Gizmos.DrawLine(dockMalePosition, dockFemalePosition);
                Gizmos.DrawWireSphere(dockMalePosition, 0.008f);
                Gizmos.DrawWireSphere(dockFemalePosition, 0.008f);
            }
        }
    }
}
