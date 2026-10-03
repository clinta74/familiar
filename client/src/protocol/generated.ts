// Generated from protocol/familiar.schema.json by client/scripts/protocol.mjs - don't edit.
// To change it, change the server's records, then regenerate (see ProtocolSchemaTests).

export interface AccountResponse {
  id: string;
  username: string;
  email: string;
  role: AccountRole;
}

/** What an account may do on this server. */
export type AccountRole = 'user' | 'admin';

/** The reply has no more clips coming: Clips were announced in total. */
export interface AudioEndEvent {
  replyId: string;
  clips: number;
}

/** A spoken sentence is ready to fetch. */
export interface AudioEvent {
  /** The reply it belongs to. */
  replyId: string;
  /** Its position in the reply; clips are played in this order. */
  seq: number;
  /** Where to fetch the audio. */
  url: string;
  /** What it says. */
  text: string;
  /** When each word is spoken, if the TTS service says - for mouth shapes. */
  words: SpokenWord[] | null;
}

export interface ConfirmRequest {
  /** The tool call being answered, from the tool.confirm event. */
  callId: string | null;
  /** Whether the user allows it. */
  approve: boolean;
}

export interface DeviceCalendarUpload {
  /** Every event in the window the phone covers (the next couple of weeks). */
  events: DeviceEventView[] | null;
}

/** One event (or occurrence) from a phone's calendar. */
export interface DeviceEventView {
  /** The phone's id for it (instance id plus start), stable across uploads. */
  externalId: string | null;
  /** Its title. */
  title: string | null;
  /** Where, if it says. */
  location: string | null;
  /** When it starts; for an all-day event, UTC midnight of its date. */
  startsAt: string;
  /** When it ends. */
  endsAt: string;
  /** A whole-day event. */
  allDay: boolean;
  /** Which of the phone's calendars ("Work", "sam@gmail.com"). */
  calendar: string | null;
}

export interface DeviceLoginRequest {
  username: string | null;
  password: string | null;
  deviceName: string | null;
}

/** Returned once, at sign-in. The token is never retrievable again. */
export interface DeviceLoginResponse {
  token: string;
  device: DeviceSummary;
  account: AccountResponse;
}

export interface DeviceSummary {
  id: string;
  name: string;
  createdAt: string;
  expiresAt: string;
  lastUsedAt: string | null;
  isCurrent: boolean;
}

/**
 * What the familiar is feeling about what it's saying. Maps one-to-one onto VRM's preset
 * expressions (happy, sad, angry, surprised, relaxed) plus neutral and thinking (PLAN.md §4.4).
 */
export type Emotion = 'neutral' | 'joy' | 'sad' | 'angry' | 'surprised' | 'relaxed' | 'thinking';

export interface ErrorEvent {
  code: string;
  message: string;
}

export interface ExpressionEvent {
  emotion: Emotion;
  intensity: number;
}

export type FamiliarState = 'idle' | 'thinking' | 'speaking' | 'resting';

export interface FamiliarSummary {
  name: string;
  expressiveness: number;
}

/** A one-off body movement the familiar makes mid-sentence. */
export type Gesture = 'nod' | 'shake' | 'tilt' | 'wave' | 'shrug' | 'laugh' | 'wink' | 'sigh' | 'yawn' | 'excited';

export interface GestureEvent {
  gesture: Gesture;
}

export interface InterruptRequest {
  /** The reply that was playing when the user interrupted. */
  replyId: string | null;
  /** What of it they heard: the reply is cut to this. */
  heard: string | null;
}

export type JobKind = 'timer' | 'reminder';

/** A pending timer or reminder, as the client shows it. */
export interface JobView {
  /** The job. */
  id: string;
  /** Timer or reminder. */
  kind: JobKind;
  /** What it's for. */
  label: string;
  /** When it goes off. */
  dueAt: string;
  /** For a timer, how long it was set for. */
  durationSeconds: number | null;
  /** When it was set. */
  createdAt: string;
  /** It rings until dismissed (timers, and alarm reminders) rather than notifying. */
  ring: boolean;
}

/**
 * The account's pending jobs, sent whenever they change, so every device's timer list stays
 * current.
 */
export interface JobsEvent {
  jobs: JobView[];
}

export type MessageRole = 'user' | 'assistant';

/** A stored message, as both the history endpoint and the message event show it. */
export interface MessageView {
  id: string;
  role: MessageRole;
  text: string;
  emotion: Emotion | null;
  intensity: number | null;
  interrupted: boolean;
  createdAt: string;
}

/** Something that just happened, for the service worker to show as a notification. */
export interface NotificationView {
  id: string;
  kind: string;
  title: string;
  body: string;
  at: string;
}

/**
 * Something went off or arrived: a timer, a reminder, an announcement from a hook. The reply that
 * says it arrives as usual; this is for the chime.
 */
export interface NotifyEvent {
  /** The job or announcement. */
  id: string;
  /** One of NotifyKinds. */
  kind: string;
  /** What it was for, or who sent it. */
  label: string;
  /** What the familiar says about it. */
  text: string;
}

export interface PushKeyResponse {
  publicKey: string;
}

export interface ReadyEvent {
  /** Its name and expressiveness. */
  familiar: FamiliarSummary;
  /** The model server answers. */
  modelAvailable: boolean;
  /** Speech-to-text and text-to-speech both answer. */
  speechAvailable: boolean;
  /** For a phone that listed capabilities: its connection, which tool.invoke events address. */
  connectionId: string | null;
}

export interface ReplyDeltaEvent {
  id: string;
  text: string;
}

export interface ReplyEndEvent {
  id: string;
  text: string;
  emotion: Emotion;
  intensity: number;
  interrupted: boolean;
}

export interface ReplyStartEvent {
  id: string;
}

/**
 * The user talked over a reply, so it was cut to what they actually heard (the model had usually
 * finished writing it long before the familiar finished saying it).
 */
export interface ReplyTruncatedEvent {
  id: string;
  text: string;
}

export interface SendMessageRequest {
  /** What the user typed. */
  text: string | null;
  /** The user's IANA time zone, for the situation in the prompt. */
  timeZone: string | null;
  /** Speak the reply as well as showing it. Spoken messages are always answered aloud. */
  speak: boolean | null;
}

export interface SpokenWord {
  word: string;
  start: number;
  end: number;
}

export interface StateEvent {
  state: FamiliarState;
}

export interface SubscribeRequest {
  /** The push service URL from the browser's PushSubscription. */
  endpoint: string | null;
  /** The browser's keys; stored, though content-free pushes don't need them. */
  keys: SubscriptionKeys | null;
}

export interface SubscriptionKeys {
  /** The browser's public key. */
  p256dh: string | null;
  /** The browser's auth secret. */
  auth: string | null;
}

/** The familiar wants to do something outside Familiar and is asking first. */
export interface ToolConfirmEvent {
  /** The reply it's part of. */
  replyId: string;
  /** Answer with this. */
  callId: string;
  /** The tool's name. */
  tool: string;
  /** What it will do, for the user to allow or not. */
  description: string;
  /** After this it counts as declined. */
  expiresAt: string;
}

/** A confirmation request was answered (by any device), so the others can put theirs away. */
export interface ToolConfirmedEvent {
  callId: string;
  approved: boolean;
}

export interface ToolFinishedEvent {
  replyId: string;
  callId: string;
  tool: string;
  ok: boolean;
  summary: string | null;
}

/** A tool call for one particular phone to carry out. */
export interface ToolInvokeEvent {
  /** Answer with this. */
  callId: string;
  /** Which connection should act; every other device ignores it. */
  connectionId: string;
  /** What to do, e.g. "calendar.add". */
  tool: string;
  /** Its arguments. */
  arguments: Record<string, unknown>;
  /** After this, don't: the server has stopped waiting (a replayed invoke must not run twice). */
  expiresAt: string;
}

export interface ToolResultRequest {
  /** The tool.invoke being answered. */
  callId: string | null;
  /** Whether it worked. */
  ok: boolean;
  /** What happened, for the model (and the summary). */
  result: Record<string, unknown> | null;
  /** Why not, when it didn't. */
  error: string | null;
}

export interface ToolStartedEvent {
  replyId: string;
  callId: string;
  tool: string;
}

/** Each event on the conversation stream and its payload, by its SSE event name (null: none). */
export interface EventPayloads {
  'ready': ReadyEvent;
  'state': StateEvent;
  'message': MessageView;
  'reply.start': ReplyStartEvent;
  'reply.delta': ReplyDeltaEvent;
  'reply.end': ReplyEndEvent;
  'reply.truncated': ReplyTruncatedEvent;
  'expression': ExpressionEvent;
  'gesture': GestureEvent;
  'audio': AudioEvent;
  'audio.end': AudioEndEvent;
  'turn.error': ErrorEvent;
  'tool.started': ToolStartedEvent;
  'tool.finished': ToolFinishedEvent;
  'tool.confirm': ToolConfirmEvent;
  'tool.confirmed': ToolConfirmedEvent;
  'tool.invoke': ToolInvokeEvent;
  'jobs': JobsEvent;
  'notify': NotifyEvent;
  'cleared': null;
  'resync': null;
}

export type EventName = keyof EventPayloads;

/** The stream's event names, as the server calls them (EventTypes). */
export const EventTypes = {
  Ready: 'ready',
  State: 'state',
  Message: 'message',
  ReplyStart: 'reply.start',
  ReplyDelta: 'reply.delta',
  ReplyEnd: 'reply.end',
  ReplyTruncated: 'reply.truncated',
  Expression: 'expression',
  Gesture: 'gesture',
  Audio: 'audio',
  AudioEnd: 'audio.end',
  Error: 'turn.error',
  ToolStarted: 'tool.started',
  ToolFinished: 'tool.finished',
  ToolConfirm: 'tool.confirm',
  ToolConfirmed: 'tool.confirmed',
  ToolInvoke: 'tool.invoke',
  Jobs: 'jobs',
  Notify: 'notify',
  Cleared: 'cleared',
  Resync: 'resync',
} as const satisfies Record<string, EventName>;
