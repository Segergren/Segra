import { forwardRef, ButtonHTMLAttributes, ReactNode } from 'react';

type ButtonVariant =
  | 'primary' // Main button style - bordered, gray text, hover primary
  | 'ghost' // Minimal/transparent
  | 'nav' // Navigation button - like primary but for nav menus
  | 'menu' // Dropdown menu item - opacity hover (default white)
  | 'menuPrimary' // Dropdown menu item - primary color with opacity hover
  | 'menuDanger' // Dropdown menu item - error/delete style
  | 'menuPurple' // Dropdown menu item - purple/highlight style
  | 'danger' // Standalone danger button
  | 'success'; // Positive action button (recover, confirm positive)

type ButtonSize = 'xs' | 'sm' | 'md' | 'lg';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: boolean; // Icon-only button (circular)
  loading?: boolean;
  children?: ReactNode;
}

const variantStyles: Record<ButtonVariant, string> = {
  primary:
    'btn btn-secondary border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 text-gray-300',
  ghost: 'btn btn-ghost text-gray-300 hover:bg-white/10',
  nav: 'btn btn-secondary w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300',
  menu: 'flex w-full items-center gap-2 px-4 py-3 text-white hover:bg-white/10 active:bg-white/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none',
  menuPrimary:
    'flex w-full items-center gap-2 px-4 py-3 text-primary hover:bg-primary/10 active:bg-primary/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none',
  menuDanger:
    'flex w-full items-center gap-2 px-4 py-3 text-error hover:bg-error/10 active:bg-error/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none',
  menuPurple:
    'flex w-full items-center gap-2 px-4 py-3 text-purple-400 hover:bg-purple-500/10 active:bg-purple-500/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none',
  danger: 'btn btn-ghost bg-error/20 hover:bg-error/10 text-error border-error',
  success: 'btn btn-ghost bg-primary/20 hover:bg-primary/10 text-primary border-primary',
};

const sizeStyles: Record<ButtonSize, string> = {
  xs: 'btn-xs',
  sm: 'btn-sm',
  md: '',
  lg: 'btn-lg',
};

// Menu variants don't use DaisyUI btn sizes, they use padding
const menuSizeStyles: Record<ButtonSize, string> = {
  xs: 'px-2 py-1.5 text-xs',
  sm: 'px-3 py-2 text-sm',
  md: 'px-4 py-3',
  lg: 'px-5 py-4 text-lg',
};

const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  (
    {
      variant = 'primary',
      size = 'md',
      icon = false,
      loading = false,
      disabled = false,
      className = '',
      children,
      ...props
    },
    ref,
  ) => {
    const isMenuVariant = variant.startsWith('menu');

    // Build the class string
    const baseStyles = variantStyles[variant];
    const sizeStyle = isMenuVariant ? '' : sizeStyles[size];
    const iconStyle = icon && !isMenuVariant ? 'btn-circle' : '';
    const disabledStyle = disabled ? 'opacity-50 cursor-not-allowed' : '';

    // For menu variants, override padding based on size
    const menuSize = isMenuVariant && size !== 'md' ? menuSizeStyles[size] : '';

    const combinedClassName = [baseStyles, sizeStyle, iconStyle, disabledStyle, menuSize, className]
      .filter(Boolean)
      .join(' ');

    return (
      <button ref={ref} className={combinedClassName} disabled={disabled || loading} {...props}>
        {loading && <span className="loading loading-spinner loading-sm w-4 h-4" />}
        {children}
      </button>
    );
  },
);

Button.displayName = 'Button';

export default Button;
