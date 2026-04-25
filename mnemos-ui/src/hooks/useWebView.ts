import { useState, useEffect, useCallback } from "react";
import type { Conversation, GraphData, GraphProgress } from "../types";

export const postToHost = (msg: string) => {
    (window as any).chrome?.webview?.postMessage(msg);
};

export function useWebView() {
    const [results, setResults] = useState<any[]>([]);
    const [activeConv, setActiveConv] = useState<Conversation | null>(null);
    const [availableFilters, setAvailableFilters] = useState<string[]>([]);
    const [isSearching, setIsSearching] = useState(false);
    const [graphData, setGraphData] = useState<GraphData | null>(null);
    const [graphProgress, setGraphProgress] = useState<GraphProgress | null>(null);

    useEffect(() => {
        const webview = (window as any).chrome?.webview;
        if (!webview) return;

        const handler = (event: MessageEvent) => {
            const data = event.data;
            if (typeof data !== "string") return;
            const sep = data.indexOf("|");
            if (sep === -1) return;
            const type = data.substring(0, sep);
            const raw = data.substring(sep + 1);

            try {
                switch (type) {
                    case "search_results":
                        setResults(JSON.parse(raw));
                        setIsSearching(false);
                        break;
                    case "conv_data":
                        setActiveConv(JSON.parse(raw));
                        break;
                    case "filters_data":
                        setAvailableFilters(JSON.parse(raw));
                        break;
                    case "graph_progress":
                        setGraphProgress(JSON.parse(raw));
                        break;
                    case "graph_data":
                        setGraphData(JSON.parse(raw));
                        setGraphProgress(null);
                        break;
                }
            } catch (e) {
                console.error("[useWebView] Parse error:", type, e);
            }
        };

        webview.addEventListener("message", handler);
        webview.postMessage("get_filters");
        webview.postMessage("get_graph_data");
        return () => webview.removeEventListener("message", handler);
    }, []);

    const search = useCallback((query: string, filter = "all") => {
        if (!query.trim()) return;
        setIsSearching(true);
        postToHost(`search:${query}|filter:${filter}`);
    }, []);

    const loadConversation = useCallback((id: string, snippet = "") => {
        postToHost(`load_conv:${id}|${snippet}`);
    }, []);

    const rebuildGraph = useCallback(() => {
        setGraphData(null);
        postToHost("rebuild_graph");
    }, []);

    const clearConversation = useCallback(() => setActiveConv(null), []);

    return {
        results,
        activeConv,
        availableFilters,
        isSearching,
        graphData,
        graphProgress,
        search,
        loadConversation,
        rebuildGraph,
        clearConversation,
    };
}