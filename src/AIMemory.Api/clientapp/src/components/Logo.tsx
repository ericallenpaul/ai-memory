import { useTheme } from '../ThemeContext'
import logoDark from '../assets/ai-memory-logo-dark-mode.png'
import logoLight from '../assets/ai-memory-logo-light-mode.png'

interface LogoProps {
  size?: number
}

export default function Logo({ size = 32 }: LogoProps) {
  const { theme } = useTheme()
  const src = theme === 'dark' ? logoDark : logoLight

  return (
    <img
      src={src}
      width={size}
      height={size}
      alt="AIMemory logo"
      style={{ objectFit: 'contain' }}
    />
  )
}
