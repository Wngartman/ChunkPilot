import type { ReactNode } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import styles from './Primitives.module.css';

export interface ActionMenuItem {
  label: string;
  icon?: ReactNode;
  disabled?: boolean;
  destructive?: boolean;
  onSelect: () => void;
}

export function ActionMenu({ label, trigger, items, open, onOpenChange }: {
  label: string;
  trigger: ReactNode;
  items: ActionMenuItem[];
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
}) {
  return <DropdownMenu.Root open={open} onOpenChange={onOpenChange} modal={false}>
    <DropdownMenu.Trigger asChild>
      <button className={styles.menuTrigger} aria-label={label} title={label}>{trigger}</button>
    </DropdownMenu.Trigger>
    <DropdownMenu.Portal>
      <DropdownMenu.Content className={styles.menuContent} side="bottom" align="end" sideOffset={6} collisionPadding={10}>
        {items.map(item => <DropdownMenu.Item
          key={item.label}
          className={`${styles.menuItem} ${item.destructive ? styles.menuItemDanger : ''}`}
          disabled={item.disabled}
          onSelect={item.onSelect}
        >
          <span className={styles.menuItemIcon} aria-hidden="true">{item.icon}</span>
          <span>{item.label}</span>
        </DropdownMenu.Item>)}
      </DropdownMenu.Content>
    </DropdownMenu.Portal>
  </DropdownMenu.Root>;
}
