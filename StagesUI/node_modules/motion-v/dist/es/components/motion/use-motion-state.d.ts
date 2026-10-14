import { MotionProps } from './types';
import { MotionBundle } from '../../state/motion-state';
import { MotionState } from '../../state';
export declare function useMotionState(props: MotionProps, bundle?: MotionBundle): {
    getProps: () => import('../..').Options<any> & {
        presenceContext: import('../animate-presence/presence').PresenceContext;
    };
    getAttrs: () => {
        [x: string]: any;
    };
    layoutGroup: import('../context').LayoutGroupState;
    state: MotionState;
};
