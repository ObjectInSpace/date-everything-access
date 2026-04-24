using System;
using System.Collections.Generic;

namespace DateEverythingAccess
{
    // Adapted from SUSMachine's typed state/event approach (MIT).
    internal sealed class EventStateMachine<TState, TEvent>
        where TState : struct
        where TEvent : struct
    {
        private readonly Dictionary<TState, Dictionary<TEvent, TState>> _transitions =
            new Dictionary<TState, Dictionary<TEvent, TState>>();

        private readonly Dictionary<TState, Action> _onEnter =
            new Dictionary<TState, Action>();

        private readonly Dictionary<TState, Action> _onExit =
            new Dictionary<TState, Action>();

        public TState CurrentState { get; private set; }

        public TState? PreviousState { get; private set; }

        internal EventStateMachine(TState initialState)
        {
            CurrentState = initialState;
            PreviousState = null;
        }

        internal void AddTransition(TState fromState, TEvent trigger, TState toState)
        {
            if (!_transitions.TryGetValue(fromState, out Dictionary<TEvent, TState> transitionMap))
            {
                transitionMap = new Dictionary<TEvent, TState>();
                _transitions[fromState] = transitionMap;
            }

            transitionMap[trigger] = toState;
        }

        internal void SetEnterAction(TState state, Action action)
        {
            if (action == null)
            {
                _onEnter.Remove(state);
                return;
            }

            _onEnter[state] = action;
        }

        internal void SetExitAction(TState state, Action action)
        {
            if (action == null)
            {
                _onExit.Remove(state);
                return;
            }

            _onExit[state] = action;
        }

        internal bool TryFire(TEvent trigger)
        {
            if (!_transitions.TryGetValue(CurrentState, out Dictionary<TEvent, TState> transitionMap) ||
                !transitionMap.TryGetValue(trigger, out TState nextState))
            {
                return false;
            }

            return SetState(nextState);
        }

        internal bool SetState(TState nextState)
        {
            if (EqualityComparer<TState>.Default.Equals(CurrentState, nextState))
                return false;

            if (_onExit.TryGetValue(CurrentState, out Action onExit))
                onExit();

            PreviousState = CurrentState;
            CurrentState = nextState;

            if (_onEnter.TryGetValue(CurrentState, out Action onEnter))
                onEnter();

            return true;
        }
    }
}
