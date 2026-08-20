declare module 'lucide-react/dist/esm/icons/*.mjs' {
  import type { ForwardRefExoticComponent, RefAttributes, SVGProps } from 'react';

  type IconProps = Omit<SVGProps<SVGSVGElement>, 'ref'> & {
    size?: number | string;
    absoluteStrokeWidth?: boolean;
  };

  const Icon: ForwardRefExoticComponent<IconProps & RefAttributes<SVGSVGElement>>;
  export default Icon;
}
