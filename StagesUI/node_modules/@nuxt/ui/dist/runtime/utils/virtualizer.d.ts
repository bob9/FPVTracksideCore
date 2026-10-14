/**
 * Get estimate size for virtualizers that checks each item individually
 */
export declare function getEstimateSize(items: any[], size: 'xs' | 'sm' | 'md' | 'lg' | 'xl' | (string & {}), descriptionKey?: string, hasDescriptionSlot?: boolean): (index: number) => number;
