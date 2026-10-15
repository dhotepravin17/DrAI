import { useEffect, useRef } from 'react'

/**
 * Opens a <dialog> as a modal when it mounts. There is deliberately no cleanup that calls close(): React's StrictMode
 * runs effect cleanups once in development, and close() fires the dialog's close event, which tells the parent to
 * unmount it, so the dialog would vanish as soon as it opened. When the component really unmounts, the element is
 * removed from the page, which takes it out of the modal layer without a close event.
 */
export function useModalDialog() {
  const dialogRef = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = dialogRef.current
    if (dialog && !dialog.open) dialog.showModal()
  }, [])
  return dialogRef
}
