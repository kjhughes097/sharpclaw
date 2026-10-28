import { Toast } from 'radix-ui';
import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';
import { localId } from '../lib/ids';

type Tone = 'info' | 'error' | 'success';
interface ToastItem {
  id: string;
  text: string;
  tone: Tone;
}

const ToastContext = createContext<((text: string, tone?: Tone) => void) | null>(null);

/** Short-lived notices (Radix Toast), announced politely to screen readers. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const show = useCallback((text: string, tone: Tone = 'info') => {
    setItems((current) => [...current.slice(-3), { id: localId('toast'), text, tone }]);
  }, []);

  return (
    <ToastContext.Provider value={show}>
      <Toast.Provider swipeDirection="down" duration={6000}>
        {children}
        {items.map((item) => (
          <Toast.Root
            key={item.id}
            className={`rounded-lg border px-4 py-3 text-sm shadow-lg ${
              item.tone === 'error'
                ? 'border-danger bg-danger-soft text-fg'
                : item.tone === 'success'
                  ? 'border-ok bg-ok-soft text-fg'
                  : 'border-border bg-surface text-fg'
            }`}
            onOpenChange={(open) => {
              if (!open) setItems((current) => current.filter((t) => t.id !== item.id));
            }}
          >
            <Toast.Description>{item.text}</Toast.Description>
          </Toast.Root>
        ))}
        <Toast.Viewport className="fixed bottom-4 left-1/2 z-50 flex w-[min(92vw,26rem)] -translate-x-1/2 flex-col gap-2 outline-none" />
      </Toast.Provider>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const show = useContext(ToastContext);
  if (!show) throw new Error('ToastProvider is missing');
  return show;
}
