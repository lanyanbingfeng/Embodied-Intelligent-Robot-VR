using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace VRSim
{
    /// <summary>
    /// 提供 XR Hands 关节（Palm/Wrist）的世界旋转，作为"拧螺丝"手势的旋转输入源。
    /// 通过订阅 XRHandSubsystem.updatedHands 事件在数据有效窗口内读取，
    /// 避免跨帧访问已被释放的 NativeArray 导致 ObjectDisposedException。
    /// 数据来自 XRHandSubsystem（PICO 上为 PXR_HandSubSystem），优先右手，可配置左右。
    /// </summary>
    [DisallowMultipleComponent]
    public class HandRotationSource : MonoBehaviour
    {
        public enum HandSide { Right, Left }
        public enum JointToUse { Palm, Wrist }

        [Header("Source")]
        public HandSide handSide = HandSide.Right;
        public JointToUse joint = JointToUse.Palm;

        [Header("Smoothing")]
        [Tooltip("0 = 使用原始数据；越大越平滑（0~0.9）")]
        [Range(0f, 0.9f)]
        public float smoothing = 0.5f;

        [Tooltip("可选：XR Origin 的 Transform，用于把设备空间姿态转换到世界空间。为空则按世界旋转处理。")]
        public Transform originTransform;

        XRHandSubsystem m_HandSubsystem;
        Quaternion m_CurrentRotation = Quaternion.identity;
        bool m_LastValid;

        /// <summary>当前手部世界旋转（平滑后）。</summary>
        public Quaternion CurrentRotation { get { return m_CurrentRotation; } }

        /// <summary>关节数据是否有效。</summary>
        public bool IsTracking { get; private set; }

        void OnEnable()
        {
            m_HandSubsystem = FindSubsystem();
            Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
            m_HandSubsystem = null;
            IsTracking = false;
            m_LastValid = false;
        }

        void Update()
        {
            // 子系统可能在运行中启动/重建，这里兜底重连
            if (m_HandSubsystem == null || !m_HandSubsystem.running)
            {
                Unsubscribe();
                m_HandSubsystem = null;
                m_HandSubsystem = FindSubsystem();
                Subscribe();
            }
        }

        void Subscribe()
        {
            if (m_HandSubsystem != null)
                m_HandSubsystem.updatedHands += OnUpdatedHands;
        }

        void Unsubscribe()
        {
            if (m_HandSubsystem != null)
                m_HandSubsystem.updatedHands -= OnUpdatedHands;
        }

        void OnUpdatedHands(XRHandSubsystem subsystem,
            XRHandSubsystem.UpdateSuccessFlags successFlags,
            XRHandSubsystem.UpdateType updateType)
        {
            // 事件回调是手部数据有效窗口：此时访问子系统的手数组是安全的
            XRHand hand = handSide == HandSide.Right ? subsystem.rightHand : subsystem.leftHand;
            XRHandJointID id = joint == JointToUse.Palm ? XRHandJointID.Palm : XRHandJointID.Wrist;

            XRHandJoint handJoint = hand.GetJoint(id);
            if (handJoint.TryGetPose(out Pose pose))
            {
                Quaternion worldRot = pose.rotation;
                if (originTransform != null)
                    worldRot = originTransform.rotation * worldRot;

                if (smoothing > 0f && m_LastValid)
                    m_CurrentRotation = Quaternion.Slerp(m_CurrentRotation, worldRot, 1f - smoothing);
                else
                    m_CurrentRotation = worldRot;

                IsTracking = true;
                m_LastValid = true;
                return;
            }

            IsTracking = false;
            m_LastValid = false;
        }

        static XRHandSubsystem FindSubsystem()
        {
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            foreach (var s in subsystems)
            {
                if (s != null && s.running)
                    return s;
            }
            foreach (var s in subsystems)
            {
                if (s != null)
                    return s;
            }
            return null;
        }
    }
}
