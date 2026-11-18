using System;
using System.Collections.Generic;
using Combat.Core;
using UnityEngine;

/// <summary>
/// Unity 与纯逻辑世界之间唯一的帧驱动器。
///
/// 执行顺序设为 10000，保证普通 PlayerController 在该类之前提交输入，
/// 然后只在这里调用一次 CombatWorld.Step()。
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class CombatSimulationRunner : MonoBehaviour
{
    private static CombatSimulationRunner instance;
    private readonly List<CombatEvent> frameEvents = new List<CombatEvent>(16);

    public static CombatWorld World { get; private set; }
    public static event Action<CombatEvent> EventCommitted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateRunner()
    {
        if (instance != null)
        {
            return;
        }

        GameObject runnerObject = new GameObject("[Combat Simulation]");
        DontDestroyOnLoad(runnerObject);
        instance = runnerObject.AddComponent<CombatSimulationRunner>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        World ??= new CombatWorld(Fix64.FromDecimal(0.05m));
    }

    private void FixedUpdate()
    {
        World.Step();
        World.DrainEvents(frameEvents);

        for (int i = 0; i < frameEvents.Count; i++)
        {
            EventCommitted?.Invoke(frameEvents[i]);
        }

        frameEvents.Clear();
    }
}
