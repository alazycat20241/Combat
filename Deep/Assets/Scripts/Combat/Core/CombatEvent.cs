namespace Combat.Core
{
    public enum CombatEventType
    {
        AttackStarted,
        HitReaction,
        Died
    }

    public readonly struct CombatEvent
    {
        public readonly long Frame;
        public readonly CombatEventType Type;
        public readonly int SourceEntityId;
        public readonly int TargetEntityId;
        public readonly AttackState AttackState;
        public readonly Fix64 Damage;

        public CombatEvent(
            long frame,
            CombatEventType type,
            int sourceEntityId,
            int targetEntityId,
            AttackState attackState = AttackState.None,
            Fix64 damage = default)
        {
            Frame = frame;
            Type = type;
            SourceEntityId = sourceEntityId;
            TargetEntityId = targetEntityId;
            AttackState = attackState;
            Damage = damage;
        }
    }
}
