using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace VRSim
{
    /// <summary>
    /// 螺丝装配总控：
    /// 1) 管理多个孔位（槽位），任意螺丝可以归到任意空槽，一个槽只能放一颗；
    /// 2) 槽位在 Awake 时由螺丝的初始摆放位置自动生成 —— 以后只移动场景里的螺丝即可调整孔位；
    /// 3) 四个槽位全空（螺丝都取走了）才解锁背板抓取，放回背板会重新锁上。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class ScrewAssemblyManager : MonoBehaviour
    {
        [Header("Slot Anchors")]
        [Tooltip("孔位坐标（世界）。用右键菜单「记录当前螺丝位置为孔位」生成，运行时不再依赖螺丝当前位置")]
        [SerializeField]
        Vector3[] slotPositions = new Vector3[0];
        [SerializeField]
        Quaternion[] slotRotations = new Quaternion[0];

        [Header("Screws")]
        [Tooltip("场景里的螺丝，顺序不重要；孔位坐标由上面的 Slot Anchors 决定")]
        [SerializeField]
        ScrewAssemblySync[] screws = new ScrewAssemblySync[0];

        [Header("Back Plate")]
        [Tooltip("背板的抓取组件，槽位全空时才允许抓取")]
        [SerializeField]
        XRGrabInteractable backPlate;
        [Tooltip("背板复位点，留空则使用背板初始位置")]
        [SerializeField]
        Transform backPlateHome;
        [Tooltip("背板放回该范围内并松手会自动归位（米）")]
        public float backPlateReturnRadius = 0.12f;

        /// <summary>一个螺丝孔位。</summary>
        public class Slot
        {
            public Vector3 position;
            public Quaternion rotation;
            public ScrewAssemblySync occupant;
        }

        readonly List<Slot> m_Slots = new List<Slot>();
        readonly List<ScrewAssemblySync> m_Occupied = new List<ScrewAssemblySync>();

        Vector3 m_BackPlateHomePos;
        Quaternion m_BackPlateHomeRot;
        Rigidbody m_BackPlateRb;
        bool m_BackPlateTaken;
        Renderer m_BackPlateRenderer;
        Color m_BackPlateOriginalColor = Color.white;
        bool m_ColorCached;
        bool? m_LastUnlockedState;

        public int SlotCount { get { return m_Slots.Count; } }

        /// <summary>所有槽位都空着（螺丝全部取走）。</summary>
        public bool AllSlotsEmpty
        {
            get
            {
                if (m_Slots.Count == 0) return false;
                for (int i = 0; i < m_Slots.Count; i++)
                    if (m_Slots[i].occupant != null) return false;
                return true;
            }
        }

        /// <summary>背板是否已经打开/取下（插头要在背板打开后才可以抓）。</summary>
        public bool IsBackPlateRemoved { get { return m_BackPlateTaken; } }

        /// <summary>当前还在孔位上的螺丝（供螺丝刀找目标）。</summary>
        public List<ScrewAssemblySync> OccupiedScrews
        {
            get
            {
                m_Occupied.Clear();
                for (int i = 0; i < m_Slots.Count; i++)
                    if (m_Slots[i].occupant != null)
                        m_Occupied.Add(m_Slots[i].occupant);
                return m_Occupied;
            }
        }

        void Awake()
        {
            EnsureInit();
        }

        /// <summary>
        /// 把当前四颗螺丝的摆放位置固化成孔位坐标（编辑器里右键菜单调用一次即可）。
        /// 运行时不再从螺丝当前位置推断孔位 —— 否则一旦脚本热重载、或螺丝已被移走，
        /// 孔位会跑到螺丝被拿走后的位置上，背板就永远解不开。
        /// </summary>
        [ContextMenu("记录当前螺丝位置为孔位")]
        void CaptureSlotsFromScrews()
        {
            if (screws == null || screws.Length == 0)
            {
                Debug.LogWarning("[ScrewAssembly] 还没绑定螺丝，无法记录孔位");
                return;
            }

            slotPositions = new Vector3[screws.Length];
            slotRotations = new Quaternion[screws.Length];
            for (int i = 0; i < screws.Length; i++)
            {
                if (screws[i] == null) continue;
                slotPositions[i] = screws[i].transform.position;
                slotRotations[i] = screws[i].transform.rotation;
            }
            Debug.Log("[ScrewAssembly] 已记录 " + slotPositions.Length + " 个孔位");
        }

        /// <summary>
        /// 建立槽位表。放在这里而不是只写在 Awake 里：
        /// 项目关闭了 Domain Reload 时，重复进入 Play 不会重新调用 Awake，
        /// m_Slots 会保持空数组，导致"螺丝都拿走了但背板永远解不开"。
        /// </summary>
        void EnsureInit()
        {
            if (m_Slots.Count > 0) return;
            if (screws == null || screws.Length == 0) return;

            bool hasAnchors = slotPositions != null && slotRotations != null
                              && slotPositions.Length == screws.Length
                              && slotRotations.Length == screws.Length;

            if (!hasAnchors)
                Debug.LogWarning("[ScrewAssembly] 孔位坐标未固化，回退为螺丝当前位置（结果可能不稳）"
                                 + "，请在 Manager 上执行右键菜单「记录当前螺丝位置为孔位」");

            m_Slots.Clear();
            for (int i = 0; i < screws.Length; i++)
            {
                var s = screws[i];
                if (s == null) continue;

                var slot = new Slot
                {
                    position = hasAnchors ? slotPositions[i] : s.transform.position,
                    rotation = hasAnchors ? slotRotations[i] : s.transform.rotation,
                    occupant = s,
                };
                m_Slots.Add(slot);
                s.BindSlot(this, slot);
            }

            if (backPlate != null)
            {
                m_BackPlateHomePos = backPlateHome != null ? backPlateHome.position : backPlate.transform.position;
                m_BackPlateHomeRot = backPlateHome != null ? backPlateHome.rotation : backPlate.transform.rotation;
                m_BackPlateRb = backPlate.GetComponent<Rigidbody>();
                m_BackPlateRenderer = backPlate.GetComponent<Renderer>();
                backPlate.selectEntered.RemoveListener(OnBackPlateGrabbed);
                backPlate.selectEntered.AddListener(OnBackPlateGrabbed);
                backPlate.enabled = false;
                ApplyBackPlateTint(false);
            }
        }

        void OnBackPlateGrabbed(SelectEnterEventArgs args)
        {
            m_BackPlateTaken = true;
        }

        void Update()
        {
            EnsureInit();
            UpdateBackPlate();
        }

        /// <summary>找最近的空槽并占用。maxDistance 之外不归位。</summary>
        public bool TryOccupy(ScrewAssemblySync screw, Vector3 near, float maxDistance, out Slot result)
        {
            result = null;
            Slot best = null;
            float bestDist = maxDistance;

            for (int i = 0; i < m_Slots.Count; i++)
            {
                var slot = m_Slots[i];
                if (slot.occupant != null) continue;
                float d = Vector3.Distance(near, slot.position);
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = slot;
                }
            }

            if (best == null) return false;

            best.occupant = screw;
            result = best;
            return true;
        }

        /// <summary>释放该螺丝占用的槽位。</summary>
        public void Release(ScrewAssemblySync screw)
        {
            for (int i = 0; i < m_Slots.Count; i++)
                if (m_Slots[i].occupant == screw)
                    m_Slots[i].occupant = null;
        }

        public Slot GetSlot(int index)
        {
            return m_Slots[index];
        }

        void UpdateBackPlate()
        {
            if (backPlate == null) return;

            if (!m_BackPlateTaken)
            {
                bool unlocked = AllSlotsEmpty;
                if (backPlate.enabled != unlocked)
                    backPlate.enabled = unlocked;
                ApplyBackPlateTint(unlocked);
                return;
            }

            if (backPlate.isSelected) return;

            float distToHome = Vector3.Distance(backPlate.transform.position, m_BackPlateHomePos);

            // 松手后离原位较远：恢复物理，让背板正常受重力掉下去
            if (distToHome > backPlateReturnRadius)
            {
                RestoreBackPlatePhysics();
                return;
            }

            // 已取下：放回原位附近就归位并重新上锁
            backPlate.transform.position = m_BackPlateHomePos;
            backPlate.transform.rotation = m_BackPlateHomeRot;
            if (m_BackPlateRb != null)
            {
                m_BackPlateRb.linearVelocity = Vector3.zero;
                m_BackPlateRb.angularVelocity = Vector3.zero;
                m_BackPlateRb.isKinematic = true;
                m_BackPlateRb.useGravity = false;
            }
            m_BackPlateTaken = false;
        }

        /// <summary>
        /// 松手后恢复重力。XRI 抓取时会把刚体设成 kinematic，松手会还原成"抓取前"的状态，
        /// 而背板抓取前就是 kinematic，于是松手后一直悬空。
        /// 切回物理前先同步刚体位置，否则会回弹到切换前的位置。
        /// </summary>
        void RestoreBackPlatePhysics()
        {
            if (m_BackPlateRb == null || backPlate == null) return;
            if (!m_BackPlateRb.isKinematic) return;

            m_BackPlateRb.position = backPlate.transform.position;
            m_BackPlateRb.rotation = backPlate.transform.rotation;
            m_BackPlateRb.isKinematic = false;
            m_BackPlateRb.useGravity = true;
        }

        /// <summary>背板可拆时染成淡绿、锁定时恢复原色，玩家一眼就能看出当前状态。</summary>
        void ApplyBackPlateTint(bool unlocked)
        {
            if (m_BackPlateRenderer == null) return;

            if (!m_ColorCached)
            {
                var mat = m_BackPlateRenderer.sharedMaterial;
                if (mat != null && mat.HasProperty("_BaseColor"))
                    m_BackPlateOriginalColor = mat.GetColor("_BaseColor");
                m_ColorCached = true;
            }

            if (m_LastUnlockedState.HasValue && m_LastUnlockedState.Value == unlocked)
                return;
            m_LastUnlockedState = unlocked;

            var mpb = new MaterialPropertyBlock();
            m_BackPlateRenderer.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", unlocked ? new Color(0.55f, 1f, 0.65f, 1f) : m_BackPlateOriginalColor);
            m_BackPlateRenderer.SetPropertyBlock(mpb);
        }
    }
}
