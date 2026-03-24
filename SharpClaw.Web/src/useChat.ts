import { useState, useCallback, useRef } from 'react';
import type { Session, ChatMessage, StreamItem, AgentEvent, ToolResultEvent } from './types';
import { createSession, sendMessage, streamEvents } from './api';

const DEFAULT_PERSONA_FILE = 'coordinator.agent.md';
const DEFAULT_PERSONA_NAME = 'Coordinator';

export interface SessionState {
    session: Session;
    personaFile: string;
    isDraft: boolean;
    messages: ChatMessage[];
    /** Stream items for the currently-streaming assistant turn */
    streamItems: StreamItem[];
    /** Completed event logs, one per assistant message (index matches assistant messages) */
    eventLogs: StreamItem[][];
    streaming: boolean;
}

export function useChat() {
    const [sessions, setSessions] = useState<SessionState[]>([]);
    const [activeIdx, setActiveIdx] = useState<number>(-1);
    const cleanupRef = useRef<(() => void) | null>(null);
    const itemCounter = useRef(0);
    const draftCounter = useRef(0);

    const active = activeIdx >= 0 ? sessions[activeIdx] : null;

    const startSession = useCallback((personaFile = DEFAULT_PERSONA_FILE, personaName = DEFAULT_PERSONA_NAME) => {
        const newState: SessionState = {
            session: {
                sessionId: `draft-${++draftCounter.current}`,
                persona: personaName,
            },
            personaFile,
            isDraft: true,
            messages: [],
            streamItems: [],
            eventLogs: [],
            streaming: false,
        };
        setSessions(prev => {
            const next = [...prev, newState];
            setActiveIdx(next.length - 1);
            return next;
        });
    }, []);

    const setDraftPersona = useCallback((idx: number, personaFile: string, personaName: string) => {
        setSessions(prev => {
            const updated = [...prev];
            const session = updated[idx];
            if (!session || !session.isDraft || session.streaming || session.messages.length > 0)
                return prev;

            updated[idx] = {
                ...session,
                personaFile,
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

    const send = useCallback(async (text: string) => {
        if (!active || active.streaming) return;
        const idx = activeIdx;
        let sessionId = active.session.sessionId;

        // Add user message
        const userMsg: ChatMessage = { role: 'user', content: text };
        setSessions(prev => {
            const updated = [...prev];
            updated[idx] = {
                ...updated[idx],
                messages: [...updated[idx].messages, userMsg],
                streamItems: [],
                streaming: true,
            };
            return updated;
        });

        try {
            if (active.isDraft) {
                const created = await createSession(active.personaFile);
                sessionId = created.sessionId;

                setSessions(prev => {
                    const updated = [...prev];
                    const current = updated[idx];
                    if (!current) return prev;

                    updated[idx] = {
                        ...current,
                        session: created,
                        isDraft: false,
                    };
                    return updated;
                });
            }

            const { messageId } = await sendMessage(sessionId, text);

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
                        setSessions(prev => {
                            const updated = [...prev];
                            const state = { ...updated[idx] };
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
                            state.streamItems = items;
                            updated[idx] = state;
                            return updated;
                        });
                        return;
                    }

                    if (event.type === 'done') {
                        setSessions(prev => {
                            const updated = [...prev];
                            const state = { ...updated[idx] };
                            // Collapse all stream tokens into a final assistant message
                            const assistantText = state.streamItems
                                .filter(i => i.event.type === 'token')
                                .map(i => (i.event as { text: string }).text)
                                .join('') || event.content;
                            state.messages = [
                                ...state.messages,
                                { role: 'assistant', content: assistantText },
                            ];
                            // Preserve event log for this turn
                            state.eventLogs = [...state.eventLogs, state.streamItems];
                            state.streamItems = [];
                            state.streaming = false;
                            updated[idx] = state;
                            return updated;
                        });
                        return;
                    }

                    // token, tool_call, permission_request
                    setSessions(prev => {
                        const updated = [...prev];
                        const state = { ...updated[idx] };
                        state.streamItems = [...state.streamItems, { id, event }];
                        updated[idx] = state;
                        return updated;
                    });

                    if (event.type === 'tool_call') {
                        toolCallMap.set(toolCallIndex++, id);
                    }
                },
                () => {
                    // SSE closed — ensure streaming is off
                    setSessions(prev => {
                        const updated = [...prev];
                        if (updated[idx]?.streaming) {
                            const state = { ...updated[idx] };
                            // Collapse any remaining tokens
                            const assistantText = state.streamItems
                                .filter(i => i.event.type === 'token')
                                .map(i => (i.event as { text: string }).text)
                                .join('');
                            if (assistantText) {
                                state.messages = [
                                    ...state.messages,
                                    { role: 'assistant', content: assistantText },
                                ];
                                // Preserve event log for this turn
                                state.eventLogs = [...state.eventLogs, state.streamItems];
                            }
                            state.streamItems = [];
                            state.streaming = false;
                            updated[idx] = state;
                        }
                        return updated;
                    });
                },
                (err) => {
                    console.error('SSE error:', err);
                    setSessions(prev => {
                        const updated = [...prev];
                        const state = { ...updated[idx] };
                        state.streaming = false;
                        updated[idx] = state;
                        return updated;
                    });
                },
            );

            cleanupRef.current = close;
        } catch (err) {
            console.error('send error:', err);
            setSessions(prev => {
                const updated = [...prev];
                updated[idx] = { ...updated[idx], streaming: false };
                return updated;
            });
        }
    }, [active, activeIdx]);

    return { sessions, active, activeIdx, startSession, setDraftPersona, selectSession, send };
}
