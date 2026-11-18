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
    -> CombatInputCommand(frame, entityId, input)
    -> CombatWorld.SubmitInput()
    -> CombatWorld.Step() 按 EntityId 排序推进
    -> 帧结束后 DrainEvents() 获取 CombatEvent
    -> PlayerController 驱动 Animator
    -> PlayerController 平滑同步 Transform
```

`CombatSimulationRunner` 是 Unity 侧唯一的逻辑帧入口。普通
`PlayerController` 只能提交输入，不能再自行调用 `PlayerEntity.Tick()`。

输入命令绑定明确的 `Frame`，过期帧和同实体同帧重复输入会被拒绝。
`CombatEvent` 包含 `Frame`、攻击者 EntityId、目标 EntityId 和伤害，并在
`CombatWorld.Step()` 全部实体完成后统一派发。

Animator 根运动不参与运行时逻辑。攻击表通过以下字段加载离线烘焙数据：

- `MotionData`：`x,y,z;x,y,z` 格式的逐帧局部位移
- `MotionLandingFrame`
- `MotionLastHorizontalFrame`
- `MotionHorizontalDistance`

编辑器菜单 `Tools > Combat > Bake JumpAttack Motion` 可在 Play Mode 中重新烘焙，
结果会复制到剪贴板并写入 `CombatAttackMotion.bake.txt`。把结果填入 Luban 后重新运行
`gen.bat`。运动字段为空时逻辑回退到 `ForwardMoveDistance`。

## 定点数学

逻辑层使用 Q32.32 `Fix64`：

- 逻辑位置、速度、旋转、HP、硬直、AABB 和伤害均为定点数
- `LogicVector3`、`LogicRotation` 不再包含 float 运算
- `FixMath` 提供确定性 Sin/Cos/Atan2
- `Fix64.Sqrt` 使用纯整数 Newton 迭代

Unity 的 float/Vector3 只出现在输入适配、Transform 同步和 Editor 工具中。
