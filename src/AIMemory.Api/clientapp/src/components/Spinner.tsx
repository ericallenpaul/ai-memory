interface SpinnerProps {
  size?: number
  label?: string
}

export default function Spinner({ size = 24, label = 'Loading...' }: SpinnerProps) {
  return (
    <span role="status" aria-label={label} style={{ display: 'inline-flex', alignItems: 'center', gap: '0.5rem' }}>
      <span
        className="spinner"
        style={{
          width: size,
          height: size,
          borderWidth: Math.max(2, Math.round(size / 10)),
        }}
      />
      {label && (
        <span className="sr-only" style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0,0,0,0)', whiteSpace: 'nowrap' }}>
          {label}
        </span>
      )}
    </span>
  )
}
