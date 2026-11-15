import React from "react";
import { Dashboard } from "./pages/Dashboard";
import "./App.css";
import "./styles/tokens.css";
import "./styles/workspace.css";
import { AppNotice } from "./components/AppNotice";

function App() {
  return <><Dashboard /><AppNotice /></>;
}

export default App;
