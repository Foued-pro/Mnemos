export type View = "list" | "constellation" | "clusters";

export interface Message {
    id: string;
    role: "User" | "Assistant";
    content: string;
    thinking?: string;
    date?: string;
}

export interface Conversation {
    conversation_id: string;
    target_snippet?: string;
    messages: Message[];
}

export interface GraphNode {
    id: string;
    label: string;
    group: number;
    val: number;
}

export interface GraphLink {
    source: string;
    target: string;
    value: number;
}

export interface GraphStats {
    conversations: number;
    messages: number;
    embedded: number;
}

export interface GraphData {
    nodes: GraphNode[];
    links: GraphLink[];
    stats?: GraphStats;
    loading?: boolean;
}

export interface GraphProgress {
    stage: "loading_conversations" | "embedding" | "linking" | "clustering" | "done" | "error";
    current: number;
    total: number;
    message: string;
}

export type WebViewMessageType =
    | "search_results"
    | "conv_data"
    | "filters_data"
    | "graph_progress"
    | "graph_data";