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

export interface AddFeedRequest {
  /** What to call it. */
  name: string | null;
  /** Its secret iCal address (https:// or webcal://). */
  url: string | null;
}

export interface AdminUserView {
  /** The account. */
  id: string;
  /** Its username. */
  username: string;
  /** Its email. */
  email: string;
  /** user or admin. */
  role: AccountRole;
  /** When it was made. */
  createdAt: string;
  /** When it last signed in, if ever. */
  lastLoginAt: string | null;
  /** It can't sign in, and its sessions and devices have stopped working. */
  isDisabled: boolean;
  /** How many phones it has signed in. */
  devices: number;
  /** The admin looking at the list. */
  isYou: boolean;
}

export interface AnnounceRequest {
  /** What to say. */
  message: string | null;
  /** "say" (word for word, the default) or "tell" (the familiar puts it in its own words). */
  style?: string | null;
  /** For "tell": the user's IANA time zone, if the caller knows it. */
  timeZone?: string | null;
}

/**
 * How a familiar looks (PLAN.md §5.5). Either the built-in plush llama, coloured and dressed, or an
 * uploaded VRM model.
 */
export interface AppearanceView {
  /** A built-in character (llama, fox, hedgehog, monster, otter, giraffe, spot) or "vrm". */
  model: 'llama' | 'fox' | 'hedgehog' | 'monster' | 'otter' | 'giraffe' | 'spot' | 'vrm';
  /** For "vrm", which uploaded model. */
  vrmId: string | null;
  /** The character's colours (what each paints depends on the character). */
  colours: LlamaColours;
  /** What it wears: none, bow, scarf, glasses, flower. The spot wears nothing. */
  accessory: 'none' | 'bow' | 'scarf' | 'glasses' | 'flower';
  /** How a built-in character is drawn: "2d" (flat, the default) or "3d" (modelled and lit). */
  style?: '2d' | '3d';
}

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
  words?: SpokenWord[] | null;
}

export interface AvatarView {
  id: string;
  name: string;
  sizeBytes: number;
  meta: VrmInfo;
  createdAt: string;
}

export type CalendarKind = 'local' | 'ics' | 'device';

/** A calendar as Settings shows it. Never the feed's address: that's a secret. */
export interface CalendarView {
  id: string;
  kind: CalendarKind;
  name: string;
  lastSyncedAt: string | null;
  lastError: string | null;
}

export interface ChangePasswordRequest {
  currentPassword: string | null;
  newPassword: string | null;
}

export interface ConfirmRequest {
  /** The tool call being answered, from the tool.confirm event. */
  callId: string | null;
  /** Whether the user allows it. */
  approve: boolean;
}

export interface CreateUserRequest {
  /** 3-24 letters, digits or underscores. */
  username: string | null;
  /** Their email. */
  email: string | null;
  /** A first password, for them to change. */
  password: string | null;
  /** user (the default) or admin. */
  role?: AccountRole | null;
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
  calendar?: string | null;
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

export interface ErrorResponse {
  error: string;
}

export interface EventView {
  id: string;
  calendarId: string;
  title: string;
  location: string | null;
  startsAt: string;
  endsAt: string;
  allDay: boolean;
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

export interface FamiliarView {
  name: string;
  userNickname: string | null;
  warmth: number;
  playfulness: number;
  verbosity: number;
  formality: number;
  expressiveness: number;
  voice?: string | null;
  voiceSpeed?: number;
  proactive?: boolean | null;
  appearance?: AppearanceView | null;
  lookUps?: boolean | null;
  homePlace?: string | null;
  home?: PlaceView | null;
}

/** A one-off body movement the familiar makes mid-sentence. */
export type Gesture = 'nod' | 'shake' | 'tilt' | 'wave' | 'shrug' | 'laugh' | 'wink' | 'sigh' | 'yawn' | 'excited';

export interface GestureEvent {
  gesture: Gesture;
}

export interface HomeAssistantRequest {
  /** Home Assistant's address, e.g. http://homeassistant.local:8123. */
  url: string | null;
  /**
   * A long-lived access token from the Home Assistant profile page. Left out, the saved one is
   * kept.
   */
  token: string | null;
}

/** The account's Home Assistant connection. */
export interface HomeAssistantView {
  /** Whether there is one; the rest is empty when not. */
  connected: boolean;
  /** The host it talks to. */
  urlHost: string | null;
  /** The devices exposed to Assist, when last asked. */
  devices: HomeDeviceView[];
  /** When the devices were last asked for. */
  refreshedAt: string | null;
  /** Why the last check went wrong, if it did. */
  lastError: string | null;
}

/** A device Familiar can work, as Settings lists it. */
export interface HomeDeviceView {
  /** What Home Assistant calls it. */
  name: string;
  /** Its room, if it has one. */
  area: string | null;
}

export interface HookTokenView {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
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

/** The plush llama's colours, as #rrggbb. Shading is derived from these by the client. */
export interface LlamaColours {
  /** The fleece. */
  wool: string;
  /** The bare face and snout. */
  face: string;
  /** Inside the ears, and the hooves. */
  ear: string;
  /** The cheeks. */
  blush: string;
  /** The accessory. */
  accent: string;
}

export interface LoginRequest {
  username: string | null;
  password: string | null;
}

export interface McpServerRequest {
  /** Short and lower-case: it prefixes the tool names the model sees. */
  name: string | null;
  /** The server's streamable-HTTP endpoint. */
  url: string | null;
  /** A bearer token, if the server needs one. */
  token: string | null;
}

export interface McpServerView {
  id: string;
  name: string;
  urlHost: string;
  tools: McpToolView[];
  refreshedAt: string | null;
  lastError: string | null;
}

export interface McpToolView {
  name: string;
  modelName: string;
  description: string | null;
  readOnly: boolean;
  enabled: boolean;
}

export interface McpToolsRequest {
  /** The tool names (as the server calls them) to switch on. */
  enabled: string[] | null;
}

export interface MemoryRequest {
  /** The memory, one short sentence. */
  text: string | null;
}

export type MemorySource = 'conversation' | 'told' | 'settings';

export interface MemoryView {
  id: string;
  text: string;
  source: MemorySource;
  createdAt: string;
  updatedAt: string;
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

export interface NameRequest {
  name: string | null;
}

export interface NewHookTokenView {
  /** The token's id. */
  id: string;
  /** What uses it. */
  name: string;
  /** Shown once, at creation. */
  token: string;
}

export interface NewWebhookView {
  /** The webhook. */
  webhook: WebhookView;
  /** The signing secret, shown once, at creation. */
  secret: string;
}

/** Something that just happened, for the service worker to show as a notification. */
export interface NotificationView {
  id: string;
  kind: 'timer' | 'reminder' | 'announcement';
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
  kind: 'timer' | 'reminder' | 'announcement';
  /** What it was for, or who sent it. */
  label: string;
  /** What the familiar says about it. */
  text: string;
}

/** A place to pick as home: one of the matches for a name, or where the device is. */
export interface PlaceView {
  /** How it's said and shown: "Warren, Michigan, United States". */
  label: string;
  /** The town: "Warren". */
  name: string;
  /** Its state, province or nation ("Michigan"), if it has one. */
  region: string | null;
  /** "United States". */
  country: string | null;
  /** ISO 3166 alpha-2: "US". */
  countryCode: string;
  /** Rounded to two places, about a kilometre. */
  latitude: number;
  /** Rounded to two places, about a kilometre. */
  longitude: number;
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
  /**
   * For a phone that listed capabilities, or a browser offering where it is: its connection, which
   * tool.invoke events address.
   */
  connectionId?: string | null;
}

export interface RegisterRequest {
  username: string | null;
  email: string | null;
  password: string | null;
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

export interface ResetPasswordRequest {
  /** The new password. */
  password: string | null;
}

export interface SendMessageRequest {
  /** What the user typed. */
  text: string | null;
  /** The user's IANA time zone, for the situation in the prompt. */
  timeZone: string | null;
  /** Speak the reply as well as showing it. Spoken messages are always answered aloud. */
  speak?: boolean | null;
}

/**
 * A sound effect, not a voice: made by the clients themselves (synthesised, so nothing to fetch),
 * the same whichever voice the familiar has. Vocal sounds (a giggle, a gasp) come later, in its
 * voice.
 */
export type SoundEffect = 'pop' | 'boing' | 'sparkle' | 'tada' | 'whoosh' | 'womp' | 'rimshot' | 'drumroll' | 'ding' | 'slide' | 'uhoh';

/** A sound effect to play now, made by the client (no audio to fetch). */
export interface SoundEvent {
  sound: SoundEffect;
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
  /** The connection it was meant for. Older apps act only on this. */
  connectionId: string;
  /** What to do, e.g. "calendar.add". */
  tool: string;
  /** Its arguments. */
  arguments: Record<string, unknown>;
  /** After this, don't: the server has stopped waiting (a replayed invoke must not run twice). */
  expiresAt: string;
  /**
   * Which phone should act, on whichever connection it has: every other device ignores it. A phone
   * coming back from the background can briefly have two (one stuck while it had no network), and
   * the newest isn't always the live one.
   */
  deviceId?: string | null;
}

export interface ToolResultRequest {
  /** The tool.invoke being answered. */
  callId: string | null;
  /** Whether it worked. */
  ok: boolean;
  /** What happened, for the model (and the summary). */
  result?: Record<string, unknown> | null;
  /** Why not, when it didn't. */
  error?: string | null;
}

export interface ToolStartedEvent {
  replyId: string;
  callId: string;
  tool: string;
}

export interface UnsubscribeRequest {
  /** The subscription to remove. */
  endpoint: string | null;
}

export interface UpdateUserRequest {
  /** A new email, or null to leave it. */
  email?: string | null;
  /** A new role, or null to leave it. */
  role?: AccountRole | null;
  /** Disable or re-enable, or null to leave it. */
  isDisabled?: boolean | null;
}

export interface VoicesView {
  /** What the TTS service offers; empty if it isn't reachable. */
  voices: string[];
  /** The voice used when a familiar hasn't chosen one. */
  default: string;
}

/** What a VRM model says about itself. */
export interface VrmInfo {
  /** "0.x" or "1.0". */
  version: string;
  /** Its title, if it has one. */
  name: string | null;
  /** Who made it. */
  authors: string[];
  /** Its licence, as the model states it (a name or a URL). */
  licence: string | null;
  /** What it says about commercial use, if anything. */
  commercialUse: string | null;
}

export interface WebhookRequest {
  name: string | null;
  url: string | null;
  events: string[] | null;
  enabled?: boolean | null;
}

/** How a test delivery went. */
export interface WebhookTestResult {
  /** The receiver answered with a success. */
  ok: boolean;
  /** What happened: the status it answered with, or why it couldn't be reached. */
  result: string;
}

export interface WebhookView {
  id: string;
  name: string;
  urlHost: string;
  events: string[];
  enabled: boolean;
  lastDeliveredAt: string | null;
  lastResult: string | null;
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
  'sound': SoundEvent;
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
  Sound: 'sound',
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
