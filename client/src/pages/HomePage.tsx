import { useRef, useState } from 'react';
import type { AvatarState } from '../avatar/types';
import { AvatarStage, type AvatarHandle } from '../components/AvatarStage';
import { ChatPanel } from '../components/ChatPanel';
import { useConversation } from '../conversation/useConversation';
import { Shell } from './Shell';

const statusText: Record<string, string> = {
  thinking: 'thinking…',
  speaking: 'talking',
  resting: 'resting — the model isn’t reachable',
};

export function HomePage() {
  const avatar = useRef<AvatarHandle>(null);
  const [typing, setTyping] = useState(false);

  const conversation = useConversation({
    onExpression: (emotion, intensity) => avatar.current?.express(emotion, intensity),
    onGesture: (gesture) => avatar.current?.gesture(gesture),
    onSpeech: (text) => avatar.current?.speak(text),
  });

  // The user typing is the text-mode equivalent of listening.
  const avatarState: AvatarState = conversation.state === 'idle' && typing ? 'listening' : conversation.state;
  const status = conversation.connection === 'reconnecting' ? 'reconnecting…' : statusText[conversation.state];

  return (
    <Shell>
      <div className="home">
        <section className="stage-panel" aria-label={conversation.familiarName}>
          <AvatarStage ref={avatar} state={avatarState} />
          <p className="familiar-name">
            {conversation.familiarName}
            {status && <span className="familiar-status"> · {status}</span>}
          </p>
        </section>
        <ChatPanel
          familiarName={conversation.familiarName}
          messages={conversation.messages}
          state={conversation.state}
          error={conversation.error}
          onSend={conversation.send}
          onInterrupt={conversation.interrupt}
          onStartOver={conversation.startOver}
          onTyping={setTyping}
        />
      </div>
    </Shell>
  );
}
