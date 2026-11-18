# Combat Core

`Combat.Core.asmdef` 设置了 `noEngineReferences: true`。该目录可以直接复制到
.NET 10 服务端项目，不依赖 Unity。

## Luban 表建议

### TbPlayerCombat

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| Id | int | 角色配置 ID |
| Name | string | 调试名称 |
| MaxHP | float | 最大生命 |
| MoveSpeed | float | 逻辑移动速度 |
| RotationSharpness | float | 逻辑转身速度 |
| HitStunDuration | float | 受击硬直秒数 |
| KnockbackFriction | float | 击退摩擦 |
| HurtBoxSize | LogicVector3 | 受击框尺寸 |
| HurtBoxLocalOffset | LogicVector3 | 受击框偏移 |
| RequiredComboHits | int | 大招所需连击命中数 |
| ComboCounterWindowFrames | int | 连击保持帧数 |
| UltimateKnockbackDistanceScale | float | 第三击击退距离比例 |
| UltimateHitNormalizedFallback | float | 无落地数据时的后备判定进度 |
| UltimateHitWindowFrames | int | 大招判定帧数 |

### TbCombatAttack

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| Id | string | 攻击配置 ID |
| OwnerId | int | 对应 TbPlayerCombat.Id |
| AttackState | AttackState | LeftPunch/RightPunch/JumpAttack |
| ActiveStartFrame | int | 判定开始帧 |
| ActiveEndFrame | int | 判定结束帧 |
| RecoveryFrames | int | 后摇帧数 |
| Damage | float | 伤害 |
| KnockbackSpeed | float | 普通命中击退速度 |
| ForwardMoveDistance | float | 无动画根数据时的前冲距离 |
| ForwardMoveStartFrame | int | 前冲开始帧 |
| ForwardMoveEndFrame | int | 前冲结束帧 |
| AttackBoxSize | LogicVector3 | 攻击框尺寸 |
| AttackBoxLocalOffset | LogicVector3 | 攻击框偏移 |

Luban 加载后调用 `CombatConfigBundle.LinkAttacksByOwner()`，把扁平攻击表组装到角色配置。

## Unity 运行时加载

`LubanCombatConfigLoader` 从 `Resources` 读取 Luban JSON：

- `Data/TbPlayerCombat`
- `Data/TbCombatAttack`

也兼容 MiniTemplate 默认的 `cfg_TbPlayerCombat`、`cfg_TbCombatAttack` 命名。
找不到 JSON 时 `PlayerController` 自动回退到 `CombatConfigDefaults`。
表名或输出目录不同，可在 PlayerController 的 Inspector 中修改两个 Resource 路径。

## 运行结构

```text
New Input System
    -> PlayerController 翻译成 CombatInputFrame
    -> PlayerEntity.Tick()
    -> CombatEvent
    -> PlayerController 驱动 Animator
    -> PlayerController 平滑同步 Transform
```

Animator 根运动只在表现层预采样为 `AttackMotionProfile`。正式锁步版本应将逐帧位移
烘焙进 Luban，避免不同设备、不同 Animator 版本产生不同结果。

`PlayerEntity` 当前仍使用 float。后续引入 Fix64 时，只需替换 `LogicVector3`、
`LogicRotation` 和帧内运算实现。
