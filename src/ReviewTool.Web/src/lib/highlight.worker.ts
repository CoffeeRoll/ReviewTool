/// <reference lib="webworker" />
// Runs syntax highlighting off the main thread so scrolling and typing never stutter.
import { highlightToLines } from './syntax';

interface Request {
  id: number;
  text: string;
  lang: string;
}

const scope = self as unknown as DedicatedWorkerGlobalScope;

scope.onmessage = async (e: MessageEvent<Request>) => {
  const { id, text, lang } = e.data;
  try {
    scope.postMessage({ id, html: await highlightToLines(text, lang) });
  } catch (err) {
    scope.postMessage({ id, html: null, error: String(err) });
  }
};
