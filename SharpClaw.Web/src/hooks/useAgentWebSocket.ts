import { useCallback, useEffect, useRef, useState } from 'react';

export interface WsOutboundMessage {
    type: 'typing' | 'response' | 'error';
    content: string | null;
    switchedTo: string | null;
}

export interface UseAgentWebSocketOptions {
    agentName: string | undefined;
    onMessage: (msg: WsOutboundMessage) => void;
    onConnected?: () => void;
    onDisconnected?: () => void;
}

export interface UseAgentWebSocketReturn {
    send: (text: string) => void;
    connected: boolean;
    sending: boolean;
}

const RECONNECT_DELAY_MS = 3000;
const HEARTBEAT_INTERVAL_MS = 30000;

export function useAgentWebSocket({
    agentName,
    onMessage,
    onConnected,
    onDisconnected,
}: UseAgentWebSocketOptions): UseAgentWebSocketReturn {
    const [connected, setConnected] = useState(false);
    const [sending, setSending] = useState(false);
    const wsRef = useRef<WebSocket | null>(null);
    const heartbeatRef = useRef<ReturnType<typeof setInterval> | null>(null);
    const reconnectRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const mountedRef = useRef(true);
    const onMessageRef = useRef(onMessage);
    onMessageRef.current = onMessage;

    const onConnectedRef = useRef(onConnected);
    onConnectedRef.current = onConnected;
    const onDisconnectedRef = useRef(onDisconnected);
    onDisconnectedRef.current = onDisconnected;

    const clearTimers = useCallback(() => {
        if (heartbeatRef.current) {
            clearInterval(heartbeatRef.current);
            heartbeatRef.current = null;
        }
        if (reconnectRef.current) {
            clearTimeout(reconnectRef.current);
            reconnectRef.current = null;
        }
    }, []);

    const connect = useCallback(() => {
        if (!agentName || !mountedRef.current) return;

        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const host = window.location.host;
        const url = `${protocol}//${host}/ws/chat/${agentName}`;

        const ws = new WebSocket(url);
        wsRef.current = ws;

        ws.onopen = () => {
            if (!mountedRef.current) return;
            setConnected(true);
            onConnectedRef.current?.();

            // Start heartbeat to detect dead connections
            heartbeatRef.current = setInterval(() => {
                if (ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify({ text: '' }));
                }
            }, HEARTBEAT_INTERVAL_MS);
        };

        ws.onmessage = (event) => {
            if (!mountedRef.current) return;
            try {
                const msg: WsOutboundMessage = JSON.parse(event.data);
                if (msg.type === 'response' || msg.type === 'error') {
                    setSending(false);
                }
                onMessageRef.current(msg);
            } catch {
                // ignore malformed messages
            }
        };

        ws.onclose = () => {
            if (!mountedRef.current) return;
            setConnected(false);
            setSending(false);
            clearTimers();
            onDisconnectedRef.current?.();

            // Auto-reconnect
            reconnectRef.current = setTimeout(() => {
                if (mountedRef.current) connect();
            }, RECONNECT_DELAY_MS);
        };

        ws.onerror = () => {
            // onclose will fire after onerror, triggering reconnect
        };
    }, [agentName, clearTimers]);

    useEffect(() => {
        mountedRef.current = true;
        connect();

        return () => {
            mountedRef.current = false;
            clearTimers();
            if (wsRef.current) {
                wsRef.current.onclose = null;
                wsRef.current.close();
                wsRef.current = null;
            }
        };
    }, [connect, clearTimers]);

    const send = useCallback((text: string) => {
        if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
        setSending(true);
        wsRef.current.send(JSON.stringify({ text }));
    }, []);

    return { send, connected, sending };
}
