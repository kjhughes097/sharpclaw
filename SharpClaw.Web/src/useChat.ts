import { useState, useCallback, useRef, useEffect } from 'react';
import type { Session, ChatMessage, PersistedSession, PersistedStreamItem, StreamItem, AgentEvent, ToolResultEvent } from './types';
import { createSession, deleteSession as deletePersistedSession, fetchSession, fetchSessions, sendMessage, streamEvents } from './api';

const DEFAULT_AGENT_ID = 'ade';
const DEFAULT_PERSONA_NAME = 'Ade';

export interface SessionState {
    session: Session;
    agentId: string;
    createdAt: string;
    lastActivityAt: string;
    isDraft: boolean;
    messages: ChatMessage[];
    /** Stream items for the currently-streaming assistant turn */
    streamItems: StreamItem[];
    /** Completed event logs, one per assistant message (index matches assistant messages) */
    eventLogs: StreamItem[][];
    streaming: boolean;
}

function removeSessionAtIndex(items: SessionState[], index: number): SessionState[] {
    return items.filter((_, itemIndex) => itemIndex !== index);
}

function restoreEventLog(items: PersistedStreamItem[], counter: React.MutableRefObject<number>): StreamItem[] {
    return items.map(item => ({
        id: `evt-${++counter.current}`,
        event: item.event,
        result: item.result,
    }));
}

function restoreEventLogs(messages: ChatMessage[], eventLogs: PersistedStreamItem[][], counter: React.MutableRefObject<number>): StreamItem[][] {
    const restored = eventLogs.map(items => restoreEventLog(items, counter));
    const assistantCount = messages.filter(message => message.role === 'assistant').length;

    while (restored.length < assistantCount)
        restored.unshift([]);

    return restored;
}

function toSessionState(session: PersistedSession, counter: React.MutableRefObject<number>): SessionState {
    return {
        session: {
            sessionId: session.sessionId,
            persona: session.persona,
        },
        agentId: session.agentId,
        createdAt: session.createdAt,
        lastActivityAt: session.lastActivityAt,
        isDraft: false,
        messages: session.messages,
        streamItems: [],
        eventLogs: restoreEventLogs(session.messages, session.eventLogs, counter),
        streaming: false,
    };
}

export function useChat(enabled: boolean) {
    const [sessions, setSessions] = useState<SessionState[]>([]);
    const [activeIdx, setActiveIdx] = useState<number>(-1);
    const cleanupRef = useRef<(() => void) | null>(null);
    const itemCounter = useRef(0);
    const draftCounter = useRef(0);

    const active = activeIdx >= 0 ? sessions[activeIdx] : null;

    function moveSessionToTopByKeys(
        items: SessionState[],
        sessionKeys: readonly string[],
        update: (session: SessionState) => SessionState,
    ): SessionState[] {
        const index = items.findIndex(session => sessionKeys.includes(session.session.sessionId));
        if (index < 0)
            return items;

        const updated = update(items[index]);
        return [updated, ...items.filter((_, itemIndex) => itemIndex !== index)];
    }

    async function reconcileSessionFromServer(sessionId: string) {
        try {
            const persisted = await fetchSession(sessionId);
            const restored = toSessionState(persisted, itemCounter);

            setSessions(prev => {
                const index = prev.findIndex(session => session.session.sessionId === sessionId);
                if (index < 0)
                    return prev;

                return [restored, ...prev.filter((_, itemIndex) => itemIndex !== index)];
            });
            setActiveIdx(0);
        } catch (err) {
            console.error('failed to reconcile session:', err);
        }
    }

    useEffect(() => {
        if (!enabled)
            return;

        let cancelled = false;

        fetchSessions()
            .then(persistedSessions => {
                if (cancelled)
                    return;

                const restored = persistedSessions.map(session => toSessionState(session, itemCounter));

                setSessions(restored);
                setActiveIdx(restored.length > 0 ? 0 : -1);
            })
            .catch(err => {
                console.error('failed to load sessions:', err);
            });

        return () => {
            cancelled = true;
        };
    }, [enabled]);

    const startSession = useCallback((agentId = DEFAULT_AGENT_ID, personaName = DEFAULT_PERSONA_NAME) => {
        const createdAt = new Date().toISOString();
        const newState: SessionState = {
            session: {
                sessionId: `draft-${++draftCounter.current}`,
                persona: personaName,
            },
            agentId,
            createdAt,
            lastActivityAt: createdAt,
            isDraft: true,
            messages: [],
            streamItems: [],
            eventLogs: [],
            streaming: false,
        };
        setSessions(prev => {
            const next = [newState, ...prev];
            setActiveIdx(0);
            return next;
        });
    }, []);

    const setDraftPersona = useCallback((idx: number, agentId: string, personaName: string) => {
        setSessions(prev => {
            const updated = [...prev];
            const session = updated[idx];
            if (!session || !session.isDraft || session.streaming || session.messages.length > 0)
                return prev;

            updated[idx] = {
                ...session,
                agentId,
                session: {
                    ...session.session,
                    persona: personaName,
                },
            };
            return updated;
        });
    }, []);

    const selectSession = useCallback((idx: number) => {
        setActiveIdx(idx);
    }, []);

    const deleteSession = useCallback(async (sessionId: string) => {
        const sessionIndex = sessions.findIndex(session => session.session.sessionId === sessionId);
        if (sessionIndex < 0)
            return;

        const session = sessions[sessionIndex];
        if (session.streaming)
            throw new Error('This chat is still streaming. Wait for it to finish before deleting it.');

        if (!session.isDraft)
            await deletePersistedSession(sessionId);

        if (active?.session.sessionId === sessionId) {
            cleanupRef.current?.();
            cleanupRef.current = null;
        }

        setSessions(prev => {
            const index = prev.findIndex(item => item.session.sessionId === sessionId);
            if (index < 0)
                return prev;

            const next = removeSessionAtIndex(prev, index);

            setActiveIdx(currentIdx => {
                if (next.length === 0)
                    return -1;
                if (currentIdx === index)
                    return Math.min(index, next.length - 1);
                if (currentIdx > index)
                    return currentIdx - 1;
                return currentIdx;
            });

            return next;
        });
    }, [active, sessions]);

    const send = useCallback(async (text: string) => {
        if (!active || active.streaming) return;
        let currentSessionKey = active.session.sessionId;
        let sessionId = active.session.sessionId;
        const trackedSessionKeys = [currentSessionKey];

        const updateTrackedSession = (update: (session: SessionState) => SessionState) => {
            setSessions(prev => moveSessionToTopByKeys(prev, trackedSessionKeys, update));
        };

        // Add user message
        const userMsg: ChatMessage = { role: 'user', content: text };
        const activityAt = new Date().toISOString();
        updateTrackedSession(session => ({
            ...session,
            messages: [...session.messages, userMsg],
            streamItems: [],
            streaming: true,
            lastActivityAt: activityAt,
        }));
        setActiveIdx(0);

        try {
            if (active.isDraft) {
                const created = await createSession(active.agentId);
                sessionId = created.sessionId;

                setSessions(prev => {
                    const createdAt = new Date().toISOString();
                    return moveSessionToTopByKeys(prev, trackedSessionKeys, session => ({
                        ...session,
                        session: created,
                        createdAt,
                        lastActivityAt: createdAt,
                        isDraft: false,
                    }));
                });

                currentSessionKey = sessionId;
                trackedSessionKeys.push(sessionId);
                setActiveIdx(0);
            }

            const { messageId } = await sendMessage(sessionId, text);
            let sawDone = false;

            // Map tool_call IDs to their stream item IDs so we can pair results
            const toolCallMap = new Map<number, string>(); // index -> streamItem id
            let toolCallIndex = 0;

            const close = streamEvents(
                sessionId,
                messageId,
                (event: AgentEvent) => {
                    const id = `evt-${++itemCounter.current}`;

                    if (event.type === 'tool_result') {
                        // Pair with the matching tool_call
                        updateTrackedSession(state => {
                            const items = [...state.streamItems];
                            // Find the last tool_call for the same tool that doesn't have a result yet
                            const pairIdx = items.findLastIndex(
                                (i: StreamItem) => i.event.type === 'tool_call' &&
                                    (i.event as { tool: string }).tool === event.tool &&
                                    !i.result
                            );
                            if (pairIdx >= 0) {
                                items[pairIdx] = { ...items[pairIdx], result: event as ToolResultEvent };
                            } else {
                                items.push({ id, event });
                            }
                            return {
                                ...state,
                                streamItems: items,
                            };
                        });
                        return;
                    }

                    if (event.type === 'done') {
                        sawDone = true;
                        const lastActivityAt = new Date().toISOString();
                        updateTrackedSession(state => {
                            const assistantText = state.streamItems
                                .filter(i => i.event.type === 'token')
                                .map(i => (i.event as { text: string }).text)
                                .join('') || event.content;

                            return {
                                ...state,
                                messages: [
                                    ...state.messages,
                                    { role: 'assistant', content: assistantText },
                                ],
                                eventLogs: [...state.eventLogs, state.streamItems],
                                streamItems: [],
                                streaming: false,
                                lastActivityAt,
                            };
                        });
                        setActiveIdx(0);
                        return;
                    }

                    // token, tool_call, permission_request
                    updateTrackedSession(state => ({
                        ...state,
                        streamItems: [...state.streamItems, { id, event }],
                    }));

                    if (event.type === 'tool_call') {
                        toolCallMap.set(toolCallIndex++, id);
                    }
                },
                () => {
                    // SSE closed — ensure streaming is off
                    const lastActivityAt = new Date().toISOString();
                    let shouldReconcile = false;
                    updateTrackedSession(state => {
                        if (!state.streaming)
                            return state;

                        const assistantText = state.streamItems
                            .filter(i => i.event.type === 'token')
                            .map(i => (i.event as { text: string }).text)
                            .join('');

                        shouldReconcile = !sawDone && !assistantText;

                        return {
                            ...state,
                            messages: assistantText
                                ? [...state.messages, { role: 'assistant', content: assistantText }]
                                : state.messages,
                            eventLogs: assistantText
                                ? [...state.eventLogs, state.streamItems]
                                : state.eventLogs,
                            streamItems: [],
                            streaming: false,
                            lastActivityAt,
                        };
                    });

                    if (shouldReconcile)
                        void reconcileSessionFromServer(sessionId);

                    setActiveIdx(0);
                },
                (err) => {
                    console.error('SSE error:', err);
                    updateTrackedSession(state => ({
                        ...state,
                        streaming: false,
                    }));
                },
            );

            cleanupRef.current = close;
        } catch (err) {
            console.error('send error:', err);
            updateTrackedSession(session => ({
                ...session,
                streaming: false,
            }));
        }
    }, [active]);

    return { sessions, active, activeIdx, startSession, setDraftPersona, selectSession, deleteSession, send };
}
