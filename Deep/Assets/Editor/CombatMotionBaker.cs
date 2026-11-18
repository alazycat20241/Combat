using System;
using System.IO;
using System.Text;
using Combat.Core;
using UnityEditor;
using UnityEngine;

namespace CombatEditor
{
    /// <summary>
    /// 仅供配置制作使用。生成的 MotionData 需要进入 Luban；
    /// 运行时不读取 Animator 根运动，也不调用这个工具。
    /// </summary>
    public static class CombatMotionBaker
    {
        [MenuItem("Tools/Combat/Bake JumpAttack Motion")]
        public static void BakeJumpAttackMotion()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("请先进入 Play Mode，再烘焙 JumpAttack 根运动。");
                return;
            }

            PlayerController player =
                Selection.activeGameObject != null
                    ? Selection.activeGameObject.GetComponent<PlayerController>()
                    : UnityEngine.Object.FindObjectOfType<PlayerController>();

            if (player == null || player.GetComponent<Animator>() == null)
            {
                Debug.LogError("请选择一个带 Animator 和 PlayerController 的角色。");
                return;
            }

            Animator animator = player.GetComponent<Animator>();
            int layer = FindAttackLayer(animator);
            if (layer < 0)
            {
                Debug.LogError("找不到包含 JumpAttack 的 Animator Layer。");
                return;
            }

            Vector3 savedPosition = animator.transform.position;
            Quaternion savedRotation = animator.transform.rotation;
            bool savedRootMotion = animator.applyRootMotion;
            AnimatorCullingMode savedCullingMode = animator.cullingMode;

            try
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = true;
                animator.Rebind();
                animator.Play(Animator.StringToHash("JumpAttack"), layer, 0f);
                animator.Update(0f);

                AnimatorStateInfo stateInfo =
                    animator.GetCurrentAnimatorStateInfo(layer);
                int frameCount = Mathf.Max(
                    1,
                    Mathf.CeilToInt(stateInfo.length / 0.05f));

                Vector3 previousRoot = animator.rootPosition;
                Quaternion inverseStartRotation =
                    Quaternion.Inverse(savedRotation);
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                float startHipsHeight = hips != null ? hips.position.y : previousRoot.y;
                float previousHipsHeight = startHipsHeight;

                LogicVector3[] frames = new LogicVector3[frameCount];
                int validCount = 0;
                int landingFrame = 0;
                int lastHorizontalFrame = 0;
                LogicVector3 cumulative = LogicVector3.Zero;
                bool wasAirborne = false;

                for (int i = 0; i < frameCount; i++)
                {
                    animator.Update(0.05f);
                    AnimatorStateInfo current =
                        animator.GetCurrentAnimatorStateInfo(layer);
                    if (current.shortNameHash !=
                            Animator.StringToHash("JumpAttack") ||
                        animator.IsInTransition(layer))
                    {
                        break;
                    }

                    Vector3 currentRoot = animator.rootPosition;
                    Vector3 localDelta =
                        inverseStartRotation * (currentRoot - previousRoot);
                    localDelta.y = 0f;

                    LogicVector3 logicDelta = new LogicVector3(
                        localDelta.x,
                        0f,
                        localDelta.z);
                    frames[validCount] = logicDelta;
                    cumulative += logicDelta;
                    validCount++;

                    if (logicDelta.SqrMagnitude > 0.000001f)
                    {
                        lastHorizontalFrame = validCount;
                    }

                    if (hips != null)
                    {
                        float currentHeight = hips.position.y;
                        float threshold = startHipsHeight + 0.03f;
                        if (currentHeight > threshold)
                        {
                            wasAirborne = true;
                        }
                        else if (wasAirborne && previousHipsHeight > threshold)
                        {
                            landingFrame = validCount;
                            wasAirborne = false;
                        }

                        previousHipsHeight = currentHeight;
                    }

                    previousRoot = currentRoot;

                    if (landingFrame > 0)
                    {
                        // 只保留冲刺到落地帧的数据，忽略之后动画回位。
                        break;
                    }
                }

                if (validCount <= 0)
                {
                    Debug.LogError("没有采样到有效根运动。");
                    return;
                }

                StringBuilder motionData = new StringBuilder();
                for (int i = 0; i < validCount; i++)
                {
                    if (i > 0)
                    {
                        motionData.Append(';');
                    }

                    motionData.Append(frames[i].X.ToString());
                    motionData.Append(',');
                    motionData.Append(frames[i].Y.ToString());
                    motionData.Append(',');
                    motionData.Append(frames[i].Z.ToString());
                }

                string result =
                    $"AttackId\tjump_attack\n" +
                    $"MotionData\t{motionData}\n" +
                    $"MotionLandingFrame\t{landingFrame}\n" +
                    $"MotionLastHorizontalFrame\t{lastHorizontalFrame}\n" +
                    "MotionHorizontalDistance\t" +
                    cumulative.Magnitude.ToString();

                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string outputPath = Path.Combine(
                    projectRoot,
                    "LubanConfig/MiniTemplate/Data/CombatAttackMotion.bake.txt");
                File.WriteAllText(outputPath, result, new UTF8Encoding(false));
                EditorGUIUtility.systemCopyBuffer = result;

                Debug.Log(
                    $"根运动烘焙完成，已复制到剪贴板并写入：{outputPath}\n{result}",
                    player);
            }
            finally
            {
                animator.applyRootMotion = savedRootMotion;
                animator.cullingMode = savedCullingMode;
                animator.Rebind();
                animator.Update(0f);
                animator.transform.SetPositionAndRotation(savedPosition, savedRotation);
            }
        }

        private static int FindAttackLayer(Animator animator)
        {
            // 当前工程的攻击状态位于 Override Layer。
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                animator.Play(Animator.StringToHash("JumpAttack"), layer, 0f);
                animator.Update(0f);
                if (animator.GetCurrentAnimatorStateInfo(layer).shortNameHash ==
                    Animator.StringToHash("JumpAttack"))
                {
                    return layer;
                }
            }

            return -1;
        }
    }
}
