// Pictures in the chat (BACKLOG item 35): shrunk on this device before they're sent - to at most
// 1024 pixels on the longer side, re-saved as JPEG. Re-saving also leaves behind what a camera
// writes beside the picture (EXIF, with its GPS position); the server strips it too, whatever sends it.

/** The longer side a picture is shrunk to: plenty for the model, a few hundred KB at most. */
export const MaxSide = 1024;

/** The size a picture is drawn at to fit [max] on its longer side, never enlarged. */
export function fitted(width: number, height: number, max = MaxSide): { width: number; height: number } {
  const scale = Math.min(1, max / Math.max(width, height));
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/** Whether something pasted or dropped is a picture this browser can draw. */
export const isPicture = (file: File | Blob) => file.type.startsWith('image/');

/** [file] shrunk to fit <see cref="MaxSide"/> and re-saved as a JPEG. Throws if the browser can't read it. */
export async function shrink(file: Blob): Promise<Blob> {
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    throw new Error('This browser can’t open that picture. Try a JPEG or PNG.');
  }
  const { width, height } = fitted(bitmap.width, bitmap.height);
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  if (!context) throw new Error('Couldn’t prepare the picture.');
  // A white ground for pictures with transparency (a PNG): JPEG has none.
  context.fillStyle = '#ffffff';
  context.fillRect(0, 0, width, height);
  context.drawImage(bitmap, 0, 0, width, height);
  bitmap.close();
  const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.85));
  if (!blob) throw new Error('Couldn’t prepare the picture.');
  return blob;
}

/** Where a sent picture is shown from: the server serves it to its owner only. */
export const pictureUrl = (imageId: string) => `/api/conversation/images/${imageId}`;
