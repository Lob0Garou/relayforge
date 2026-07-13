import { render,screen } from '@testing-library/react'; import { describe,it,expect } from 'vitest'; import { StatusBadge } from '../src/components';
describe('StatusBadge',()=>{it('uses visible text in addition to color',()=>{render(<StatusBadge status="DeadLettered"/>);expect(screen.getByText('Dead letter')).toBeVisible();});});
