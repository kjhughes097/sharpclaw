import type { components } from './schema';

type S = components['schemas'];

export type AgentDto = S['AgentDto'];
export type TokenMetaDto = S['TokenMetaDto'];
export type TokenValueDto = S['TokenValueDto'];
export type ThreadDto = S['ThreadDto'];
export type ThreadSummaryDto = S['ThreadSummaryDto'];
export type ThreadListResponse = S['ThreadListResponse'];
export type ThreadDetailResponse = S['ThreadDetailResponse'];
export type PendingApprovalDto = S['PendingApprovalDto'];
export type MessageDto = S['MessageDto'];
export type MessageListResponse = S['MessageListResponse'];
export type ChatToolCall = S['ChatToolCall'];
export type ApprovalDetailDto = S['ApprovalDetailDto'];
export type LlmCallDto = S['LlmCallDto'];
export type ToolCallDto = S['ToolCallDto'];
export type TraceResponse = S['TraceResponse'];
export type TraceTurnDto = S['TraceTurnDto'];
export type TraceToolCallDto = S['TraceToolCallDto'];
export type SystemInfoResponse = S['SystemInfoResponse'];
export type SessionInfoResponse = S['SessionInfoResponse'];
export type UserDto = S['UserDto'];
export type ApiErrorResponse = S['ApiErrorResponse'];
export type UsageResponse = S['UsageResponse'];
export type UsageBucketDto = S['UsageBucketDto'];
export type UsageByModelDto = S['UsageByModelDto'];
export type ActivityResponse = S['ActivityResponse'];
export type ActivityItemDto = S['ActivityItemDto'];
export type CreditsResponse = S['CreditsResponse'];
export type TraceLlmCallDto = S['TraceLlmCallDto'];

/** `GET /api/usage` ranges. */
export type UsageRange = 'today' | '7d' | '30d';

/** Risk levels in increasing order (3.2); the last needs a second confirmation. */
export const RISK_LEVELS = ['low', 'medium', 'high', 'critical'] as const;
export const HIGHEST_RISK = 'critical';

/** Agent names, matching the server's validation. */
export const AGENT_NAME_PATTERN = /^[a-z0-9][a-z0-9-]{1,31}$/;

/** `Messages:MaxContentChars` on the gateway. */
export const MAX_CONTENT_CHARS = 32000;
