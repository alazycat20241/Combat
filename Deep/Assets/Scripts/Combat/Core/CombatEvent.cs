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
        public readonly CombatEventType Type;
        public readonly AttackState AttackState;

        public CombatEvent(CombatEventType type, AttackState attackState = AttackState.None)
        {
            Type = type;
            AttackState = attackState;
        }
    }
}
