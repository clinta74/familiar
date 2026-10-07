import type { ToolInvokeEvent, ToolResultRequest } from '../protocol/generated';

/** The capability a browser offers, and the tool the server asks for (BACKLOG item 24). */
export const LOCATION = 'location.read';

/** Whether this browser can say where it is: the Geolocation API, which only works over HTTPS. */
export const canLocate = (): boolean =>
  typeof navigator !== 'undefined' && 'geolocation' in navigator && typeof window !== 'undefined' && window.isSecureContext;

/** Two decimal places: about a kilometre, enough for the weather and no closer. */
export const rounded = (latitude: number, longitude: number): string => `${latitude.toFixed(2)},${longitude.toFixed(2)}`;

/**
 * Answers the server's request for where this browser is. The browser has no geocoder, so the
 * answer is rounded coordinates, which the weather tools take as a place; the familiar is told not
 * to read them out. A position from the last ten minutes will do.
 */
export async function answerLocation(
  invoke: ToolInvokeEvent,
  position: () => Promise<GeolocationPosition> = () =>
    new Promise((resolve, reject) =>
      navigator.geolocation.getCurrentPosition(resolve, reject, { enableHighAccuracy: false, maximumAge: 600_000, timeout: 8_000 })),
): Promise<ToolResultRequest> {
  try {
    const { coords } = await position();
    return {
      callId: invoke.callId,
      ok: true,
      result: {
        coordinates: rounded(coords.latitude, coords.longitude),
        note: `From their web browser, which gives coordinates, not a place name: don't read them out. For the weather or air here, pass these coordinates themselves as the place (place: "${rounded(coords.latitude, coords.longitude)}"), not a town guessed from them.`,
      },
    };
  } catch (error) {
    const denied = typeof GeolocationPositionError !== 'undefined' && error instanceof GeolocationPositionError
      ? error.code === error.PERMISSION_DENIED
      : (error as { code?: number } | null)?.code === 1;
    return {
      callId: invoke.callId,
      ok: false,
      error: denied
        ? "Their browser isn't allowing this site to know where they are. They can allow location for it in the browser's site settings."
        : "Their browser couldn't tell where it is just now.",
    };
  }
}
