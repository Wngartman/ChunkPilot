import { createRoot } from 'react-dom/client';
import App from './app/App';
import './design-system/tokens.css';

createRoot(document.getElementById('root')!).render(<App />);
