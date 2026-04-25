interface Window {
    chrome?: {
        webview?: {
            postMessage: (msg: string) => void;
            addEventListener: (type: string, handler: (e: MessageEvent) => void) => void;
            removeEventListener: (type: string, handler: (e: MessageEvent) => void) => void;
        };
    };
}